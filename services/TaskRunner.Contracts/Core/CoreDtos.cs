namespace TaskRunner.Contracts.Core;

public class OpenVaultRequest
{
    public string? Path { get; set; }
}

public class UpdateServerAddressRequest
{
    public string? Domain { get; set; }
    public string? DisplayName { get; set; }
}

public class NotesMdBatchAddRequest
{
    public List<string> Paths { get; set; } = new();
}