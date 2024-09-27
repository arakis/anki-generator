using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;

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

    static void ParseCsv()
    {
        string inputFile = "/home/sebastian/tmp/anki-generator/original-words.csv";
        string outputFile = "processed-words.csv";

        using (var reader = new StreamReader(inputFile))
        using (var writer = new StreamWriter(outputFile, false, Encoding.UTF8))
        {
            // Write header
            writer.WriteLine("German,Wolof,Expanded German");

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
                    string expandedGerman = ExpandGerman(german);

                    writer.WriteLine($"\"{german}\",\"{wolof}\",\"{expandedGerman}\"");
                }
            }
        }

        Console.WriteLine($"Processed CSV file has been generated: {outputFile}");
    }

    static string ExpandGerman(string german)
    {
        if (!german.Contains("("))
            return german;

        if (german.Contains(" ("))
        {
            // Remove the part in parentheses
            return Regex.Replace(german, @"\s*\([^)]*\)", "").Trim();
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

                return string.Join(", ", expandedForms);
            }
        }

        return german;
    }
}