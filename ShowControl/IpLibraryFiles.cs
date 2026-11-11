using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.VisualBasic.FileIO;

namespace ShowControl;

internal static class IpLibraryFiles
{
    public const int MaxFileSize = 8 * 1024 * 1024;
    private const int MaxWorkbookSize = 24 * 1024 * 1024;
    private const int MaxRows = 12_801;
    private static readonly string[] Headers = ["IP 分组", "IP 地址", "节点名称", "模拟状态", "分组 ID",
        "健康模式", "查询端口", "查询指令", "预期回执", "默认目标端口", "本机发送端口", "独立接收端口",
        "网络Ping", "回执匹配", "查询超时ms", "单独设置", "回执源端口", "接收编码", "查询编码", "查询回执编码"];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    public static (byte[] Content, string ContentType, string FileName) Export(IpGroup[] groups, string format)
    {
        CheckGroups(groups);
        var rows = new[] { Headers }.Concat(groups.SelectMany(group => group.Nodes.Length == 0
            ? new[] { new[] { group.Name, "", "", "", group.Id } }
            : group.Nodes.Select(node => new[] { group.Name, node.Ip, node.Label, node.Simulation, group.Id,
                node.HealthQuery is null ? "ping" : "udp", node.HealthQuery?.Port.ToString(CultureInfo.InvariantCulture) ?? "",
                node.HealthQuery?.Payload ?? "", node.HealthQuery?.Reply ?? "",
                node.Communication?.TargetPort?.ToString(CultureInfo.InvariantCulture) ?? "",
                node.Communication?.SenderPort.ToString(CultureInfo.InvariantCulture) ?? "0",
                node.Communication?.ReceiverPort?.ToString(CultureInfo.InvariantCulture) ?? "",
                node.PingEnabled ? "1" : "0", node.HealthQuery?.Match ?? "exact",
                node.HealthQuery?.TimeoutMs.ToString(CultureInfo.InvariantCulture) ?? "800", node.Customized ? "1" : "0",
                node.Communication?.ReplySourcePort?.ToString(CultureInfo.InvariantCulture) ?? "", node.Communication?.ReceiveEncoding ?? "utf8",
                node.HealthQuery?.Encoding ?? "utf8", node.HealthQuery?.ReplyEncoding ?? "utf8" })));
        return format switch
        {
            "xlsx" => (WriteWorkbook(rows), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "IP分组库.xlsx"),
            "txt" => (WriteText(rows), "text/plain; charset=utf-8", "IP分组库.txt"),
            _ => throw new ArgumentException("导出格式须为 .xlsx 或 .txt。")
        };
    }

    public static IpGroup[] Import(Stream file, string fileName, IpGroup[] current)
    {
        CheckGroups(current);
        try
        {
            var table = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".xlsx" => ReadWorkbook(file),
                ".txt" => ReadText(file),
                _ => throw new ArgumentException("请选择 .xlsx 或 .txt 文件。")
            };
            if (table.Length == 0)
                throw new ArgumentException("文件没有可导入的内容。");
            return Merge(current, Parse(table, Path.GetExtension(fileName).Equals(".txt", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) when (error is OpenXmlPackageException or InvalidDataException or
            XmlException or MalformedLineException or DecoderFallbackException)
        {
            throw new ArgumentException("无法读取文件。请使用标准 .xlsx 或 UTF-8 TXT，可先导出一份作为模板。", error);
        }
    }

    private static IpGroup[] Merge(IpGroup[] current, IEnumerable<ImportedNode> rows)
    {
        var result = current.ToList();
        var matched = new HashSet<string>();
        var updates = new Dictionary<IPAddress, Node>();
        foreach (var block in rows.GroupBy(row => row.Id.Length > 0 ? "id:" + row.Id : "name:" + row.Group))
        {
            var first = block.First();
            if (block.Any(row => row.Group != first.Group))
                throw new ArgumentException($"第 {first.Line} 行的分组 ID 对应多个分组名称。");

            var existing = current.FirstOrDefault(group => group.Id == first.Id);
            if (existing is null)
            {
                var names = current.Where(group => group.Name == first.Group).ToArray();
                if (names.Length > 1)
                    throw new ArgumentException($"第 {first.Line} 行的分组「{first.Group}」有重名，请使用导出的分组 ID。");
                existing = names.SingleOrDefault();
            }
            if (existing is not null && !matched.Add(existing.Id))
                throw new ArgumentException($"第 {first.Line} 行与文件中的另一组指向同一个现有分组。");

            var group = existing ?? new IpGroup(Guid.NewGuid().ToString(), first.Group, []);
            var nodes = group.Nodes.ToDictionary(node => ProjectRules.ParseIp(node.Ip));
            var imported = new HashSet<IPAddress>();
            foreach (var row in block)
            {
                if (row.Ip is null)
                    continue;
                if (!imported.Add(row.Ip))
                    throw new ArgumentException($"第 {row.Line} 行的 IP 地址在同一分组内重复。");
                nodes.TryGetValue(row.Ip, out var previous);
                nodes[row.Ip] = new Node(previous?.Ip ?? row.Ip.ToString(),
                    row.Label ?? previous?.Label ?? "", row.Simulation ?? previous?.Simulation ?? "ok",
                    row.HealthSpecified ? row.Query : previous?.HealthQuery,
                    row.CommunicationSpecified ? row.Communication : previous?.Communication,
                    row.PingEnabled ?? previous?.PingEnabled ?? true, row.Customized ?? previous?.Customized ?? false);
                if (updates.TryGetValue(row.Ip, out var other) && other != nodes[row.Ip])
                    throw new ArgumentException($"第 {row.Line} 行的同一 IP 在多个分组中的节点设置不一致。");
                updates[row.Ip] = nodes[row.Ip];
            }
            var updated = group with { Nodes = nodes.Values.ToArray() };
            if (existing is null)
                result.Add(updated);
            else
                result[result.IndexOf(existing)] = updated;
        }
        var groups = result.Select(group => group with
        {
            Nodes = group.Nodes.Select(node => updates.GetValueOrDefault(ProjectRules.ParseIp(node.Ip)) ?? node).ToArray()
        }).ToArray();
        CheckGroups(groups);
        return groups;
    }

    private static ImportedNode[] Parse(TableRow[] table, bool allowPlainText)
    {
        var header = table[0].Cells;
        var groupColumn = FindColumn(header, "IP 分组", "分组", "group");
        var ipColumn = FindColumn(header, "IP 地址", "IP", "ip address");
        var labelColumn = FindColumn(header, "节点名称", "名称", "label");
        var simulationColumn = FindColumn(header, "模拟状态", "simulation");
        var idColumn = FindColumn(header, "分组 ID", "group id");
        var healthColumn = FindColumn(header, "健康模式", "health mode");
        var portColumn = FindColumn(header, "查询端口", "query port");
        var queryColumn = FindColumn(header, "查询指令", "query command");
        var replyColumn = FindColumn(header, "预期回执", "expected reply");
        var targetColumn = FindColumn(header, "默认目标端口");
        var senderColumn = FindColumn(header, "本机发送端口");
        var receiverColumn = FindColumn(header, "独立接收端口");
        var sourceColumn = FindColumn(header, "回执源端口");
        var receiveEncodingColumn = FindColumn(header, "接收编码");
        var queryEncodingColumn = FindColumn(header, "查询编码");
        var replyEncodingColumn = FindColumn(header, "查询回执编码");
        var pingColumn = FindColumn(header, "网络Ping");
        var matchColumn = FindColumn(header, "回执匹配");
        var timeoutColumn = FindColumn(header, "查询超时ms");
        var customColumn = FindColumn(header, "单独设置");
        var plainText = allowPlainText && groupColumn < 0 &&
            table.All(row => row.Cells.Skip(1).All(string.IsNullOrWhiteSpace));
        if (!plainText && (groupColumn < 0 || ipColumn < 0))
            throw new ArgumentException("首行须包含「IP 分组」和「IP 地址」列。可先导出一份作为模板。");

        return table.Skip(!plainText || ipColumn == 0 ? 1 : 0).Select(row =>
        {
            var group = plainText ? "导入节点" : row.Get(groupColumn);
            var ip = row.Get(plainText ? 0 : ipColumn);
            var label = row.Get(labelColumn);
            var simulation = row.Get(simulationColumn);
            var healthMode = row.Get(healthColumn).ToLowerInvariant();
            HealthQuery? query = null;
            if (healthMode == "udp")
            {
                if (!int.TryParse(row.Get(portColumn), out var port) || port is < 1 or > 65535 ||
                    string.IsNullOrWhiteSpace(row.Get(queryColumn)) || string.IsNullOrWhiteSpace(row.Get(replyColumn)))
                    throw new ArgumentException($"第 {row.Line} 行的 UDP 健康模式须填写查询端口、查询指令和预期回执。");
                var match = row.Get(matchColumn);
                var timeout = OptionalPort(timeoutColumn) ?? 800;
                query = new HealthQuery(port, row.Raw(queryColumn), row.Raw(replyColumn), match.Length == 0 ? "exact" : match, timeout,
                    row.Get(queryEncodingColumn) is { Length: > 0 } queryEncoding ? queryEncoding : "utf8",
                    row.Get(replyEncodingColumn) is { Length: > 0 } replyEncoding ? replyEncoding : "utf8");
            }
            if (group.Length is 0 or > 80 || label.Length > 80 ||
                simulation is not ("" or "ok" or "pingFail" or "timeout") ||
                healthMode is not ("" or "ping" or "udp") ||
                (ip.Length == 0 && (label.Length != 0 || simulation.Length != 0 || healthMode.Length != 0)))
                throw new ArgumentException($"第 {row.Line} 行的分组、节点或模拟状态无效。");
            try
            {
                return new ImportedNode(group, row.Get(idColumn), ip.Length > 0 ? ProjectRules.ParseIp(ip) : null,
                    label.Length == 0 ? null : label, simulation.Length == 0 ? null : simulation,
                    healthMode.Length > 0, query, row.Line,
                    targetColumn >= 0 || senderColumn >= 0 || receiverColumn >= 0 || sourceColumn >= 0 || receiveEncodingColumn >= 0,
                    new NodeCommunication(OptionalPort(targetColumn), OptionalPort(senderColumn) ?? 0, OptionalPort(receiverColumn), OptionalPort(sourceColumn),
                        row.Get(receiveEncodingColumn) is { Length: > 0 } receiveEncoding ? receiveEncoding : "utf8"),
                    OptionalBool(pingColumn), OptionalBool(customColumn));
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"第 {row.Line} 行：{error.Message}", error);
            }

            int? OptionalPort(int column)
            {
                var value = row.Get(column);
                if (value.Length == 0) return null;
                if (!int.TryParse(value, out var number) || number is < 0 or > 65535)
                    throw new ArgumentException($"第 {row.Line} 行包含无效端口或超时。");
                return number;
            }
            bool? OptionalBool(int column)
            {
                var value = row.Get(column);
                return value switch { "" => null, "1" => true, "0" => false,
                    _ => throw new ArgumentException($"第 {row.Line} 行的开关须为 1 或 0。") };
            }
        }).ToArray();
    }

    private static int FindColumn(string?[] header, params string[] names)
    {
        static string Normalize(string? value) => string.Concat((value ?? "").Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();
        var keys = names.Select(Normalize).ToHashSet();
        return Array.FindIndex(header, value => keys.Contains(Normalize(value)));
    }

    private static TableRow[] ReadText(Stream file)
    {
        using var reader = new TextFieldParser(file, Utf8, detectEncoding: true, leaveOpen: true);
        reader.SetDelimiters("\t");
        reader.HasFieldsEnclosedInQuotes = true;
        reader.TrimWhiteSpace = false;
        var rows = new List<TableRow>();
        while (!reader.EndOfData)
        {
            var line = reader.LineNumber;
            var cells = reader.ReadFields();
            if (cells?.Any(cell => !string.IsNullOrWhiteSpace(cell)) == true)
                rows.Add(new TableRow(line, cells));
            if (rows.Count > MaxRows)
                throw new ArgumentException("导入文件的有效行过多。");
        }
        return rows.ToArray();
    }

    private static TableRow[] ReadWorkbook(Stream file)
    {
        using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > 2000 || archive.Entries.Sum(entry => entry.Length) > MaxWorkbookSize)
                throw new ArgumentException("Excel 展开后的内容超出导入限制。");
        }
        file.Position = 0;
        using var document = SpreadsheetDocument.Open(file, false, new OpenSettings { MaxCharactersInPart = MaxWorkbookSize });
        var book = document.WorkbookPart;
        var sheetId = book?.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault()?.Id?.Value;
        if (book is null || sheetId is null || book.GetPartById(sheetId) is not WorksheetPart sheet)
            throw new ArgumentException("Excel 文件需要至少一个工作表。");
        var strings = book.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            .Select(item => item.InnerText).ToArray() ?? [];
        var rows = new List<TableRow>();
        foreach (var row in sheet.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            var cells = new string?[64];
            var fallback = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnIndex(cell.CellReference?.Value, fallback);
                fallback = column + 1;
                if (column < cells.Length)
                    cells[column] = CellValue(cell, strings);
            }
            if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                rows.Add(new TableRow(row.RowIndex?.Value ?? (uint)rows.Count + 1, cells));
            if (rows.Count > MaxRows)
                throw new ArgumentException("导入文件的有效行过多。");
        }
        return rows.ToArray();
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference))
            return fallback;
        var value = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z')
                break;
            value = value * 26 + ch - 'A' + 1;
            if (value > 64)
                return 64;
        }
        return Math.Max(0, value - 1);
    }

    private static string? CellValue(Cell cell, string[] strings)
    {
        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.InnerText;
        var text = cell.CellValue?.Text;
        if (cell.DataType?.Value != CellValues.SharedString)
            return text;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= strings.Length)
            throw new InvalidDataException("Invalid Excel shared-string reference.");
        return strings[index];
    }

    private static byte[] WriteWorkbook(IEnumerable<string[]> rows)
    {
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook))
        {
            var book = document.AddWorkbookPart();
            var part = book.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            foreach (var row in rows)
                data.Append(new Row(row.Select(value => new Cell(new InlineString(
                    new Text(value) { Space = SpaceProcessingModeValues.Preserve })) { DataType = CellValues.InlineString })));
            // Entitize carriage returns so Excel's XML parsing cannot turn a UDP CRLF into LF.
            using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { NewLineHandling = NewLineHandling.Entitize }))
                new Worksheet(data).WriteTo(writer);
            book.Workbook = new Workbook(new Sheets(new Sheet { Id = book.GetIdOfPart(part), SheetId = 1, Name = "IP分组库" }));
        }
        return buffer.ToArray();
    }

    private static byte[] WriteText(IEnumerable<string[]> rows)
    {
        static string Quote(string value) => value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        return [.. Utf8.GetPreamble(), .. Utf8.GetBytes(string.Join("\r\n", rows.Select(row => string.Join('\t', row.Select(Quote)))) + "\r\n")];
    }

    private static void CheckGroups(IpGroup[] groups) =>
        ProjectRules.Check(new Project("IP 分组库", [], groups, [], [], []));

    private sealed record TableRow(long Line, string?[] Cells)
    {
        public string Raw(int index) => index >= 0 && index < Cells.Length ? Cells[index] ?? "" : "";
        public string Get(int index) => Raw(index).Trim();
    }

    private sealed record ImportedNode(string Group, string Id, IPAddress? Ip, string? Label, string? Simulation,
        bool HealthSpecified, HealthQuery? Query, long Line, bool CommunicationSpecified, NodeCommunication Communication,
        bool? PingEnabled, bool? Customized);
}
