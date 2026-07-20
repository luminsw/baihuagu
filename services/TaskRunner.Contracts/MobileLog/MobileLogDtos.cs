namespace TaskRunner.Contracts.MobileLog;

public class MobileLogDto
{
    public string Id { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Level { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Context { get; set; } = "";
    public Dictionary<string, string>? Extra { get; set; }
    public DateTime ServerTimestamp { get; set; }
}

public class MobileDeviceDto
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int LogCount { get; set; }
    public DateTime LastLogTime { get; set; }
    public int ErrorCount { get; set; }
    public int WarnCount { get; set; }
}

public class MobileLogStatsDto
{
    public int Total { get; set; }
    public int Info { get; set; }
    public int Warn { get; set; }
    public int Error { get; set; }
    public int Devices { get; set; }
}

public class MobileLogRequest
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Level { get; set; }
    public string Message { get; set; } = "";
    public string? Timestamp { get; set; }
    public string? Context { get; set; }
    public Dictionary<string, string>? Extra { get; set; }
}

public class BatchLogRequest
{
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public List<MobileLogRequest> Logs { get; set; } = new();
}

public class MobileLogQuery
{
    public string? DeviceId { get; set; }
    public string? Level { get; set; }
    public int Limit { get; set; } = 100;
    public int Offset { get; set; } = 0;
}