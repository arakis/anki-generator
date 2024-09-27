using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
    static void Main(string[] args)
    {
        // ParseXml();
        ParseCsv();
    }

    static void ParseXml()
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

    static string EscapeCsvField(string field)
    {
        if (field.Contains("\"") || field.Contains(",") || field.Contains("\n"))
        {
            return field.Replace("\"", "\"\"");
        }

        return field;
    }

    static string cacheFile = "frequency_cache.json";

    static void ParseCsv()
    {
        string inputFile = "/home/sebastian/tmp/anki-generator/original-words.csv";
        string outputFile = "processed-words.csv";

        var frequencyCache = LoadFrequencyCache(cacheFile);

        using (var reader = new StreamReader(inputFile))
        using (var writer = new StreamWriter(outputFile, false, Encoding.UTF8))
        {
            // Write header
            writer.WriteLine("German,Wolof,Order");

            // Skip header
            reader.ReadLine();

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(',');
                if (parts.Length >= 2)
                {
                    string german = parts[0].Trim('"');
                    string wolof = parts[1].Trim('"');
                    var (expandedGerman, frequencyData) = ExpandGermanWithFrequency(german, frequencyCache);

                    if (frequencyData == null)
                    {
                        frequencyData = new FrequencyData
                        {
                            Frequency = 0,
                            Hits = 0,
                            Total = 0
                        };
                    }

                    writer.WriteLine($"\"{german}\",\"{wolof}\",\"{frequencyData.Order}\"");
                }
            }
        }

        SaveFrequencyCache(frequencyCache, cacheFile);
        Console.WriteLine($"Processed CSV file has been generated: {outputFile}");
    }

    static (string[], FrequencyData) ExpandGermanWithFrequency(string german, Dictionary<string, FrequencyData?> cache)
    {
        string[] expandedWords = ExpandGerman(german);

        FrequencyData maxFrequencyData = null;
        var frequencyData = GetFrequencyFromApi(expandedWords, cache);
        if (maxFrequencyData == null || frequencyData.Frequency > maxFrequencyData.Frequency)
            maxFrequencyData = frequencyData;

        return (expandedWords, maxFrequencyData);
    }

    static FrequencyData? GetFrequencyFromApi(string[] words, Dictionary<string, FrequencyData?> cache)
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
            SaveFrequencyCache(cache, cacheFile);
            return null;
        }
    }

    static Dictionary<string, FrequencyData?> LoadFrequencyCache(string cacheFile)
    {
        if (File.Exists(cacheFile))
        {
            string json = File.ReadAllText(cacheFile);
            return JsonSerializer.Deserialize<Dictionary<string, FrequencyData?>>(json);
        }

        return new Dictionary<string, FrequencyData?>();
    }

    static void SaveFrequencyCache(Dictionary<string, FrequencyData?> cache, string cacheFile)
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

    class FrequencyData
    {
        public int Hits { get; set; }

        public long Total { get; set; }

        public int Frequency { get; set; }
        public long Order => Hits == 0 ? 0 : Total / Hits;

        public string Query { get; set; }
    }

    static string[] ExpandGerman(string german)
    {
        var newList = new List<string>();
        var words = ExpandGerman2(german);
        foreach (var word in words)
        {
            if (word.Contains(','))
            {
                newList.Add(word.Split(',')[0]);
                break;
            }
            else if (word.Contains(' '))
            {
                newList.Add(word.Split(' ')[0]);
                break;
            }
            else if (word.Contains('('))
            {
                newList.Add(word.Split('(')[0]);
                break;
            }
            else
            {
                newList.Add(word);
            }
        }

        return newList.ToArray();
    }

    static string[] ExpandGerman2(string german)
    {
        if (!german.Contains("("))
            return [german];

        if (german.Contains(" ("))
        {
            // Remove the part in parentheses
            return [Regex.Replace(german, @"\s*\([^)]*\)", "").Trim()];
        }
        else
        {
            // Expand the word
            var match = Regex.Match(german, @"(\w+)\(([^)]+)\)");
            if (match.Success)
            {
                string baseWord = match.Groups[1].Value;
                string endings = match.Groups[2].Value;

                var expandedForms = new List<string>();
                foreach (var ending in endings.Split(','))
                {
                    string trimmedEnding = ending.Trim();
                    if (trimmedEnding.StartsWith("-"))
                    {
                        expandedForms.Add(baseWord + trimmedEnding.Substring(1));
                    }
                    else
                    {
                        expandedForms.Add(baseWord + trimmedEnding);
                    }
                }

                return expandedForms.ToArray();
            }
        }

        return [german];
    }
}