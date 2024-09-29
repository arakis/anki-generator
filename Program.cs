using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CsvHelper;
using CsvHelper.Configuration;
using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json.Serialization;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Please provide a deck name as a parameter.");
            return;
        }

        string deckName = args[0];
        var processor = new DeckProcessor(deckName);
        processor.ProcessDeck();
    }
}

public class DeckProcessor
{
    private readonly string _deckName;
    private readonly string _deckDir;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, FrequencyData?> _frequencyCache;
    private readonly string _projectDir;

    public DeckProcessor(string deckName)
    {
        _projectDir = GetProjectDirectory();
        _deckName = deckName;
        _deckDir = Path.Combine(_projectDir, "decks", deckName);
        _httpClient = new HttpClient();
        _frequencyCache = LoadFrequencyCache();
    }

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
    
    public void ProcessDeck()
    {
        string inputFile = Path.Combine(_deckDir, "original-words.csv");
        string outputFile = Path.Combine(_deckDir, "anki-deck.csv");
        string overridesCsvPath = Path.Combine(_deckDir, "overrides.csv");
        string extraCsvPath = Path.Combine(_deckDir, "extra.csv");
        string frequencyCacheFile = Path.Combine(_deckDir, "frequency_cache.json");

        var overrides = LoadOverrides(overridesCsvPath);
        var processedWords = ReadAndProcessInputCsv(inputFile);
        ApplyFrequencyOrderAndOverrides(processedWords, overrides);

        var extraWords = LoadExtraWords(extraCsvPath);
        processedWords.InsertRange(0, extraWords);

        RecalculateWordOrder(processedWords);
        WriteProcessedWordsToFile(processedWords, outputFile);

        SaveFrequencyCache();
        Console.WriteLine($"Processed CSV file has been generated: {outputFile}");
    }

    private List<FrontBackOrder> ReadAndProcessInputCsv(string inputFile)
    {
        var processedWords = new List<FrontBackOrder>();
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true };

        using var reader = new StreamReader(inputFile);
        using var csv = new CsvReader(reader, csvConfig);

        var records = csv.GetRecords<FrontBack>();

        foreach (var record in records)
        {
            var (_, frequencyData) = GetGermanWordFrequency(record.Front);

            var processedWord = new FrontBackOrder
            {
                Front = record.Front,
                Back = record.Back,
                Order = frequencyData?.Order ?? long.MaxValue
            };

            processedWords.Add(processedWord);
        }

        return processedWords;
    }

    private (string[], FrequencyData?) GetGermanWordFrequency(string germanWord)
    {
        string[] expandedWords = ExpandGermanWord(germanWord);
        var frequencyData = GetFrequencyFromApi(expandedWords);
        return (expandedWords, frequencyData);
    }

    private FrequencyData? GetFrequencyFromApi(string[] words)
    {
        string query = string.Join("|", words);
        if (_frequencyCache.TryGetValue(query, out FrequencyData? frequencyData))
            return frequencyData;

        try
        {
            string url = $"https://dwds.de/api/frequency/?q={HttpUtility.UrlEncode(query)}";
            string json = _httpClient.GetStringAsync(url).Result;
            var response = JsonSerializer.Deserialize<FrequencyResponse>(json);
            frequencyData = new FrequencyData
            {
                Hits = response.Hits,
                Total = long.Parse(response.Total),
                Frequency = response.Frequency,
                Query = query
            };
            _frequencyCache[query] = frequencyData;
            return frequencyData;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching frequency data: {ex.Message}");
            _frequencyCache[query] = null;
            return null;
        }
    }

    private Dictionary<string, FrequencyData?> LoadFrequencyCache()
    {
        string frequencyCacheFile = Path.Combine(_deckDir, "frequency_cache.json");
        if (File.Exists(frequencyCacheFile))
        {
            string json = File.ReadAllText(frequencyCacheFile);
            return JsonSerializer.Deserialize<Dictionary<string, FrequencyData?>>(json);
        }

        return new Dictionary<string, FrequencyData?>();
    }

    private void SaveFrequencyCache()
    {
        string frequencyCacheFile = Path.Combine(_deckDir, "frequency_cache.json");
        string json = JsonSerializer.Serialize(_frequencyCache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(frequencyCacheFile, json);
    }

    private void WriteProcessedWordsToFile(List<FrontBackOrder> words, string outputFile)
    {
        using var writer = new StreamWriter(outputFile, false, Encoding.UTF8);
        using var csvWriter = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { Delimiter = "\t" });

        writer.WriteLine("#separator:tab");
        writer.WriteLine("#html:true");
        writer.WriteLine("#guid column:1");
        writer.WriteLine("#notetype column:2");
        writer.WriteLine("#deck column:3");
        writer.WriteLine("#tags column:6");

        foreach (var word in words)
        {
            var id = GeneratePersistentId(word.Front);
            var outputWord = new DeckOutputRecord
            {
                Id = id,
                NoteType = "Einfach (beide Richtungen)",
                DeckName = _deckName,
                Front = word.Front,
                Back = word.Back,
                Tags = "",
            };

            csvWriter.WriteRecord(outputWord);
            csvWriter.NextRecord();
        }
    }

    private string GeneratePersistentId(string german)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            var inputBytes = Encoding.UTF8.GetBytes(german);
            var hashBytes = sha256.ComputeHash(inputBytes);
            return ConvertToBase36(hashBytes).Substring(0, 10);
        }
    }

    private string ConvertToBase36(byte[] bytes)
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

    private static void ApplyFrequencyOrderAndOverrides(List<FrontBackOrder> words, List<Override> overrides)
    {
        words.Sort((a, b) => a.Order.CompareTo(b.Order));

        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            word.Order = i;

            // Apply override if exists
            var over = overrides.FirstOrDefault(o => word.Front.StartsWith(o.Front, StringComparison.OrdinalIgnoreCase));
            if (over != null)
            {
                word.Order = over.Order;
            }
        }

        words.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private static void RecalculateWordOrder(List<FrontBackOrder> words)
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

    private static List<FrontBackOrder> LoadExtraWords(string extraCsvPath)
    {
        var extraWords = new List<FrontBackOrder>();
        if (File.Exists(extraCsvPath))
        {
            using (var reader = new StreamReader(extraCsvPath))
            using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
            {
                csv.Read();
                csv.ReadHeader();

                while (csv.Read())
                {
                    extraWords.Add(new FrontBackOrder
                    {
                        Front = csv.GetField("Front"),
                        Back = csv.GetField("Back"),
                        Order = csv.GetField<int>("Order"),
                    });
                }
            }
        }

        return extraWords;
    }
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

public class FrequencyData
{
    public required int Hits { get; init; }

    public required long Total { get; init; }

    public required int Frequency { get; init; }
    public long Order => Hits == 0 ? 0 : Total / Hits;

    public required string Query { get; init; }
}

public class FrontBackOrder : FrontBack
{
    public long Order { get; set; }
}

public class DeckOutputRecord
{
    public required string Id { get; init; } // Changed from Guid to string
    public required string NoteType { get; init; }
    public required string DeckName { get; init; }
    public required string Front { get; init; }
    public required string Back { get; init; }
    public required string Tags { get; init; }
}

public class Override
{
    public required string Front { get; init; }
    public required int Order { get; init; }
}

public class FrontBack
{
    public required string Front { get; init; }
    public required string Back { get; init; }
}