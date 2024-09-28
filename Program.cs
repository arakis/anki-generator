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

public class Program
{
    private static void Main(string[] args)
    {
        // ParseXmlDictionary(); // Uncomment to parse XML dictionary
        ProcessWordList();
    }

    private static readonly string ProjectDir = GetProjectDirectory();
    private static readonly string OverridesCsvPath = Path.Combine(ProjectDir, "overrides.csv");
    private static readonly string ExtraCsvPath = Path.Combine(ProjectDir, "extra.csv");
    private static readonly string FrequencyCacheFile = Path.Combine(ProjectDir, "frequency_cache.json");

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
    
    private static void ParseXmlDictionary()
    {
        string htmlContent = File.ReadAllText("path-to-xml");
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        var paragraphs = doc.DocumentNode.SelectNodes("//p[@class='index' or @class='indext']");

        using (var writer = new StreamWriter("output.csv", false, Encoding.UTF8))
        {
            writer.WriteLine("German,Wolof");

            foreach (var paragraph in paragraphs)
            {
                var germanWord = paragraph.SelectSingleNode(".//span[@class='color2']")?.InnerText.Trim();
                var wolofTranslation = paragraph.SelectSingleNode("span/text()[last()]")?.InnerText.Trim();

                if (germanWord != null && wolofTranslation != null)
                {
                    germanWord = HttpUtility.HtmlDecode(germanWord);
                    wolofTranslation = HttpUtility.HtmlDecode(wolofTranslation);

                    writer.WriteLine($"\"{EscapeCsvField(germanWord)}\",\"{EscapeCsvField(wolofTranslation)}\"");
                }
            }
        }

        Console.WriteLine("CSV file has been generated: output.csv");
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains("\"") || field.Contains(",") || field.Contains("\n"))
        {
            return field.Replace("\"", "\"\"");
        }

        return field;
    }

    private static void ProcessWordList()
    {
        string inputFile = Path.Combine(ProjectDir, "original-words.csv");
        string outputFile = Path.Combine(ProjectDir, "anki-deck.csv");

        var frequencyCache = LoadFrequencyCache(FrequencyCacheFile);
        var overrides = LoadOverrides(OverridesCsvPath);

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
                var (_, frequencyData) = GetGermanWordFrequency(germanWord, frequencyCache);

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
        var extraWords = LoadExtraWords(ExtraCsvPath);
        processedWords.InsertRange(0, extraWords);

        // Recalculate final order
        RecalculateWordOrder(processedWords);

        // Write processed words to output CSV
        WriteProcessedWordsToFile(processedWords, outputFile, csvConfig);

        SaveFrequencyCache(frequencyCache, FrequencyCacheFile);
        Console.WriteLine($"Processed CSV file has been generated: {outputFile}");
    }

    private static (string[], FrequencyData) GetGermanWordFrequency(string germanWord, Dictionary<string, FrequencyData?> cache)
    {
        string[] expandedWords = ExpandGermanWord(germanWord);
        var frequencyData = GetFrequencyFromApi(expandedWords, cache);
        return (expandedWords, frequencyData);
    }

    private static FrequencyData? GetFrequencyFromApi(string[] words, Dictionary<string, FrequencyData?> cache)
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
            SaveFrequencyCache(cache, FrequencyCacheFile);
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
        public int Hits { get; set; }

        [JsonPropertyName("total")]
        public string Total { get; set; }

        [JsonPropertyName("frequency")]
        public int Frequency { get; set; }

        [JsonPropertyName("q")]
        public string Q { get; set; }
    }

    private class FrequencyData
    {
        public int Hits { get; set; }

        public long Total { get; set; }

        public int Frequency { get; set; }
        public long Order => Hits == 0 ? 0 : Total / Hits;

        public string Query { get; set; }
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

    private static void WriteProcessedWordsToFile(List<ProcessedWord> words, string outputFile, CsvConfiguration config)
    {
        using (var writer = new StreamWriter(outputFile, false, Encoding.UTF8))
        using (var csvWriter = new CsvWriter(writer, config))
        {
            foreach (var word in words)
            {
                var outputWord = new ProcessedWordOutput
                {
                    German = word.German,
                    Wolof = word.Wolof
                };

                csvWriter.WriteRecord(outputWord);
                csvWriter.NextRecord();
            }
        }
    }
}

public class ProcessedWord
{
    public string German { get; set; }
    public string Wolof { get; set; }
    public long Order { get; set; }
}

public class ProcessedWordOutput
{
    public string German { get; set; }
    public string Wolof { get; set; }
}

// Add this class to represent an override
public class Override
{
    public string Word { get; set; }
    public int Order { get; set; }
}