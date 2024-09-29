using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;
using System.Security.Cryptography;
using System.Numerics;

public class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Please provide a deck name as a parameter.");
            return;
        }

        string deckName = args[0];
        ProcessWordList(deckName);
    }

    private static readonly string ProjectDir = GetProjectDirectory();

    // Add this method to determine the project directory
    private static string GetProjectDirectory()
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!File.Exists(Path.Combine(currentDir, "anki-generator.csproj")))
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
            if (currentDir == null)
            {
                throw new DirectoryNotFoundException("Could not find the project directory.");
            }
        }

        return currentDir;
    }

    private static void ProcessWordList(string deckName)
    {
        string deckDir = Path.Combine(ProjectDir, "decks", deckName);
        Directory.CreateDirectory(deckDir);

        string inputFile = Path.Combine(deckDir, "original-words.csv");
        string outputFile = Path.Combine(deckDir, $"anki-deck.csv");
        string overridesCsvPath = Path.Combine(deckDir, "overrides.csv");
        string extraCsvPath = Path.Combine(deckDir, "extra.csv");
        string frequencyCacheFile = Path.Combine(deckDir, "frequency_cache.json");

        var frequencyCache = LoadFrequencyCache(frequencyCacheFile);
        var overrides = LoadOverrides(overridesCsvPath);

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
        };

        var processedWords = new List<ProcessedWord>();

        // Read and process input CSV
        using (var reader = new StreamReader(inputFile))
        using (var csv = new CsvReader(reader, csvConfig))
        {
            csv.Read();
            csv.ReadHeader();

            while (csv.Read())
            {
                string germanWord = csv.GetField(0);
                string wolofTranslation = csv.GetField(1);
                var (_, frequencyData) = GetGermanWordFrequency(germanWord, frequencyCache, frequencyCacheFile);

                var processedWord = new ProcessedWord
                {
                    German = germanWord,
                    Wolof = wolofTranslation,
                    Order = frequencyData?.Order ?? long.MaxValue // Use max value for words without frequency data
                };

                processedWords.Add(processedWord);
            }
        }

        // Sort words by frequency and apply overrides
        ApplyFrequencyOrderAndOverrides(processedWords, overrides);

        // Add extra words at the beginning
        var extraWords = LoadExtraWords(extraCsvPath);
        processedWords.InsertRange(0, extraWords);

        // Recalculate final order
        RecalculateWordOrder(processedWords);

        // Write processed words to output CSV
        WriteProcessedWordsToFile(processedWords, outputFile, deckName);

        SaveFrequencyCache(frequencyCache, frequencyCacheFile);
        Console.WriteLine($"Processed CSV file has been generated: {outputFile}");
    }

    private static (string[], FrequencyData) GetGermanWordFrequency(string germanWord, Dictionary<string, FrequencyData?> cache, string frequencyCacheFile)
    {
        string[] expandedWords = ExpandGermanWord(germanWord);
        var frequencyData = GetFrequencyFromApi(expandedWords, cache, frequencyCacheFile);
        return (expandedWords, frequencyData);
    }

    private static FrequencyData? GetFrequencyFromApi(string[] words, Dictionary<string, FrequencyData?> cache, string frequencyCacheFile)
    {
        string query = string.Join("|", words);
        if (cache.TryGetValue(query, out FrequencyData? frequencyData))
            return frequencyData;

        try
        {
            string url = $"https://dwds.de/api/frequency/?q={HttpUtility.UrlEncode(query)}";
            using (var client = new System.Net.WebClient())
            {
                string json = client.DownloadString(url);
                var response = JsonSerializer.Deserialize<FrequencyResponse>(json);
                frequencyData = new FrequencyData
                {
                    Hits = response.Hits,
                    Total = long.Parse(response.Total),
                    Frequency = response.Frequency,
                    Query = query
                };
                cache[query] = frequencyData;
                return frequencyData;
            }
        }
        catch (Exception ex)
        {
            cache[query] = null;
            SaveFrequencyCache(cache, frequencyCacheFile);
            return null;
        }
    }

    private static Dictionary<string, FrequencyData?> LoadFrequencyCache(string cacheFile)
    {
        if (File.Exists(cacheFile))
        {
            string json = File.ReadAllText(cacheFile);
            return JsonSerializer.Deserialize<Dictionary<string, FrequencyData?>>(json);
        }

        return new Dictionary<string, FrequencyData?>();
    }

    private static void SaveFrequencyCache(Dictionary<string, FrequencyData?> cache, string cacheFile)
    {
        string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(cacheFile, json);
    }

    public class FrequencyResponse
    {
        [JsonPropertyName("hits")]
        public required int Hits { get; init; }

        [JsonPropertyName("total")]
        public required string Total { get; init; }

        [JsonPropertyName("frequency")]
        public required int Frequency { get; init; }

        [JsonPropertyName("q")]
        public required string Q { get; init; }
    }

    private class FrequencyData
    {
        public required int Hits { get; init; }

        public required long Total { get; init; }

        public required int Frequency { get; init; }
        public long Order => Hits == 0 ? 0 : Total / Hits;

        public required string Query { get; init; }
    }

    private static string[] ExpandGermanWord(string germanWord)
    {
        var expandedForms = new List<string>();
        var words = ExpandGermanWordWithParentheses(germanWord);

        foreach (var word in words)
        {
            // Take only the first part before any comma, space, or opening parenthesis
            var simplifiedWord = word.Split(new[] { ',', ' ', '(' })[0];
            expandedForms.Add(simplifiedWord);
            break; // We only need the first simplified form
        }

        return expandedForms.ToArray();
    }

    private static string[] ExpandGermanWordWithParentheses(string germanWord)
    {
        if (!germanWord.Contains("("))
            return new[] { germanWord };

        if (germanWord.Contains(" ("))
        {
            // Remove the part in parentheses for words like "das Haus (Häuser)"
            return new[] { Regex.Replace(germanWord, @"\s*\([^)]*\)", "").Trim() };
        }
        else
        {
            // Expand words with internal parentheses like "Haus(es)"
            var match = Regex.Match(germanWord, @"(\w+)\(([^)]+)\)");
            if (match.Success)
            {
                string baseWord = match.Groups[1].Value;
                string endings = match.Groups[2].Value;

                return endings.Split(',')
                    .Select(ending => baseWord + (ending.Trim().StartsWith("-") ? ending.Trim().Substring(1) : ending.Trim()))
                    .ToArray();
            }
        }

        return new[] { germanWord };
    }

    private static void ApplyFrequencyOrderAndOverrides(List<ProcessedWord> words, List<Override> overrides)
    {
        words.Sort((a, b) => a.Order.CompareTo(b.Order));

        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            word.Order = i;

            // Apply override if exists
            var over = overrides.FirstOrDefault(o => word.German.StartsWith(o.Word, StringComparison.OrdinalIgnoreCase));
            if (over != null)
            {
                word.Order = over.Order;
            }
        }

        words.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private static void RecalculateWordOrder(List<ProcessedWord> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            words[i].Order = i;
        }
    }

    // Add this method to load overrides
    private static List<Override> LoadOverrides(string overridesCsvPath)
    {
        var overrides = new List<Override>();
        if (File.Exists(overridesCsvPath))
        {
            using (var reader = new StreamReader(overridesCsvPath))
            using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                overrides = csv.GetRecords<Override>().ToList();
            }
        }

        return overrides;
    }

    private static List<ProcessedWord> LoadExtraWords(string extraCsvPath)
    {
        var extraWords = new List<ProcessedWord>();
        if (File.Exists(extraCsvPath))
        {
            using (var reader = new StreamReader(extraCsvPath))
            using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                csv.Read();
                csv.ReadHeader();

                while (csv.Read())
                {
                    extraWords.Add(new ProcessedWord
                    {
                        German = csv.GetField("Front"),
                        Wolof = csv.GetField("Back"),
                        Order = csv.GetField<int>("Order"),
                    });
                }
            }
        }

        return extraWords;
    }

    private static void WriteProcessedWordsToFile(List<ProcessedWord> words, string outputFile, string deckName)
    {
        using (var writer = new StreamWriter(outputFile, false, Encoding.UTF8))
        using (var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "\t" }))
        {
            writer.WriteLine("#separator:tab");
            writer.WriteLine("#html:true");
            writer.WriteLine("#guid column:1");
            writer.WriteLine("#notetype column:2");
            writer.WriteLine("#deck column:3");
            writer.WriteLine("#tags column:6");

            foreach (var word in words)
            {
                var id = GeneratePersistentId(word.German);
                var outputWord = new ProcessedWordOutput
                {
                    Id = id,
                    NoteType = "Einfach (beide Richtungen)",
                    DeckName = deckName,
                    German = word.German,
                    Wolof = word.Wolof,
                    Tags = "",
                };

                csvWriter.WriteRecord(outputWord);
                csvWriter.NextRecord();
            }
        }
    }

    private static string GeneratePersistentId(string german)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            var inputBytes = Encoding.UTF8.GetBytes(german);
            var hashBytes = sha256.ComputeHash(inputBytes);
            return ConvertToBase36(hashBytes).Substring(0, 10);
        }
    }

    private static string ConvertToBase36(byte[] bytes)
    {
        var base36Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var result = new StringBuilder();
        var value = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);

        while (value > 0)
        {
            value = BigInteger.DivRem(value, 36, out var remainder);
            result.Insert(0, base36Chars[(int)remainder]);
        }

        return result.ToString();
    }
}

public class ProcessedWord
{
    public required string German { get; init; }
    public required string Wolof { get; init; }
    public long Order { get; set; }
}

public class ProcessedWordOutput
{
    public required string Id { get; init; } // Changed from Guid to string
    public required string NoteType { get; init; }
    public required string DeckName { get; init; }
    public required string German { get; init; }
    public required string Wolof { get; init; }
    public required string Tags { get; init; }
}

// Add this class to represent an override
public class Override
{
    public required string Word { get; init; }
    public required int Order { get; init; }
}