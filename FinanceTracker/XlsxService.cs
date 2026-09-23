using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using FinanceTracker.Localization;
using FinanceTracker.Models;

namespace FinanceTracker.Services
{
    /// Кросс-платформенный (Windows + Android) минимальный читатель/писатель XLSX.
    /// Использует только BCL (System.IO.Compression + System.Xml.Linq) — работает
    /// без ClosedXML и System.Drawing.
    public static class XlsxService
    {
        private const string ContentTypesXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
              "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
              "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
              "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
              "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>";

        private const string RootRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
              "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string BuildWorkbookXml()
        {
            return
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                      "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
              "<sheets>" +
                "<sheet name=\"" + EscapeXml(Localizer.Instance["Excel_SheetName"]) + "\" sheetId=\"1\" r:id=\"rId1\"/>" +
              "</sheets>" +
            "</workbook>";
        }

        private const string WorkbookRelsXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
              "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "</Relationships>";

        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        // ===================== ЭКСПОРТ =====================

        public static void Export(Stream stream, IEnumerable<ExpenseItem> items)
        {
            // ZipArchive требует seekable-поток, поэтому работаем через MemoryStream
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "[Content_Types].xml", ContentTypesXml);
                WriteEntry(zip, "_rels/.rels", RootRelsXml);
                WriteEntry(zip, "xl/workbook.xml", BuildWorkbookXml());
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRelsXml);
                WriteEntry(zip, "xl/worksheets/sheet1.xml", BuildSheetXml(items));
            }
            buffer.Position = 0;
            buffer.CopyTo(stream);
            stream.Flush();
        }

        private static string BuildSheetXml(IEnumerable<ExpenseItem> items)
        {
            var sb = new StringBuilder(2048);
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"").Append(MainNs).Append("\"><sheetData>");

            // Заголовок
            sb.Append("<row r=\"1\">");
            AppendInlineString(sb, "A1", Localizer.Instance["Excel_Column_Name"]);
            AppendInlineString(sb, "B1", Localizer.Instance["Excel_Column_Amount"]);
            AppendInlineString(sb, "C1", Localizer.Instance["Excel_Column_Date"]);
            AppendInlineString(sb, "D1", Localizer.Instance["Excel_Column_Category"]);
            sb.Append("</row>");

            int row = 2;
            foreach (var item in items)
            {
                sb.Append("<row r=\"").Append(row).Append("\">");
                AppendInlineString(sb, "A" + row, item.Name ?? string.Empty);
                AppendNumber(sb, "B" + row, item.Amount);
                // Дату храним как ISO-строку — так проще и читается одинаково везде.
                AppendInlineString(sb, "C" + row, item.Date ?? string.Empty);
                AppendInlineString(sb, "D" + row, item.Category ?? string.Empty);
                sb.Append("</row>");
                row++;
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static void AppendInlineString(StringBuilder sb, string cellRef, string value)
        {
            sb.Append("<c r=\"").Append(cellRef).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
            sb.Append(EscapeXml(value));
            sb.Append("</t></is></c>");
        }

        private static void AppendNumber(StringBuilder sb, string cellRef, double value)
        {
            sb.Append("<c r=\"").Append(cellRef).Append("\"><v>");
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            sb.Append("</v></c>");
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var s = entry.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(content);
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }

        // ===================== ИМПОРТ =====================

        public static List<ExpenseItem> Import(Stream source)
        {
            var result = new List<ExpenseItem>();

            // Приводим к seekable-потоку (важно для Android content:// URI)
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            buffer.Position = 0;

            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

            var sheetEntry = zip.GetEntry("xl/worksheets/sheet1.xml");
            if (sheetEntry is null)
                return result;

            var sharedStrings = LoadSharedStrings(zip);

            XDocument doc;
            using (var sheetStream = sheetEntry.Open())
                doc = XDocument.Load(sheetStream);

            XNamespace ns = MainNs;

            bool headerSkipped = false;
            foreach (var row in doc.Descendants(ns + "row"))
            {
                if (!headerSkipped) { headerSkipped = true; continue; }

                string name = string.Empty;
                string date = string.Empty;
                string category = string.Empty;
                double amount = 0;
                bool hasAmount = false;

                foreach (var cell in row.Elements(ns + "c"))
                {
                    string cellRef = (string?)cell.Attribute("r") ?? string.Empty;
                    string col = new string(cellRef.Where(char.IsLetter).ToArray()).ToUpperInvariant();
                    string type = (string?)cell.Attribute("t") ?? string.Empty;

                    string text = GetCellText(cell, ns, type, sharedStrings);

                    switch (col)
                    {
                        case "A":
                            name = text.Trim();
                            break;
                        case "B":
                            if (TryReadNumber(cell, ns, text, out var av))
                            {
                                amount = av;
                                hasAmount = true;
                            }
                            break;
                        case "C":
                            // Дата может прийти либо как строка (наш формат),
                            // либо как Excel serial date (число) — если открывали чужой файл.
                            if ((string.IsNullOrEmpty(type) || type == "n") &&
                                TryReadNumber(cell, ns, text, out var serial) &&
                                serial > 1 && serial < 2958466) // ~ 1900..9999
                            {
                                try { date = DateTime.FromOADate(serial).ToString("yyyy-MM-dd"); }
                                catch { date = text.Trim(); }
                            }
                            else
                            {
                                date = text.Trim();
                            }
                            break;
                        case "D":
                            category = text.Trim();
                            break;
                    }
                }

                if (string.IsNullOrWhiteSpace(name) || !hasAmount)
                    continue;

                // Нормализация даты
                string normalizedDate;
                if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtInv))
                    normalizedDate = dtInv.ToString("yyyy-MM-dd");
                else if (DateTime.TryParse(date, out var dt))
                    normalizedDate = dt.ToString("yyyy-MM-dd");
                else
                    normalizedDate = DateTime.Today.ToString("yyyy-MM-dd");

                if (string.IsNullOrWhiteSpace(category))
                    category = "Другое";

                result.Add(new ExpenseItem
                {
                    Name = name,
                    Amount = amount,
                    Date = normalizedDate,
                    Category = category
                });
            }

            return result;
        }

        private static List<string> LoadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return list;

            XDocument doc;
            using (var s = entry.Open())
                doc = XDocument.Load(s);

            XNamespace ns = MainNs;
            foreach (var si in doc.Descendants(ns + "si"))
                list.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));

            return list;
        }

        private static string GetCellText(XElement cell, XNamespace ns, string type, List<string> sharedStrings)
        {
            switch (type)
            {
                case "s":
                    {
                        string v = cell.Element(ns + "v")?.Value ?? string.Empty;
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                            idx >= 0 && idx < sharedStrings.Count)
                            return sharedStrings[idx];
                        return string.Empty;
                    }
                case "inlineStr":
                    return string.Concat(cell.Descendants(ns + "t").Select(t => t.Value));
                case "str":
                    return cell.Element(ns + "v")?.Value ?? string.Empty;
                default:
                    return cell.Element(ns + "v")?.Value ?? string.Empty;
            }
        }

        private static bool TryReadNumber(XElement cell, XNamespace ns, string text, out double value)
        {
            string v = cell.Element(ns + "v")?.Value ?? text;
            return double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }
}