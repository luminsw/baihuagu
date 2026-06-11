namespace TaskRunner.Controllers
{
    public class VaultRootPathPreferenceResponse
    {
        public string VaultRootPath { get; set; } = "";
    }

    public class AddVaultRequest
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Industry { get; set; }
    }

    public class UpdateVaultRequest
    {
        public string? Name { get; set; }
        public bool? IsPaid { get; set; }
        public string? Tags { get; set; }
        public string? Industry { get; set; }
    }
}
