namespace FlowMind.Domain.Entities;

/// <summary>
/// What the admin decided about one unanswered question. The question TEXT is never stored here: only a hash of its normalised form,
/// so a visitor who deletes their chat also removes the question from the report. A question asked again after the decision reopens.
/// </summary>
public class QuestionGapStatus : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    /// <summary>SHA-256 (hex) of the normalised question.</summary>
    public string Key { get; set; } = "";
    /// <summary>"handled" (the documentation was fixed) or "ignored" (not something the documents should cover).</summary>
    public string Status { get; set; } = "handled";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A visitor or signed-in user asked a person for help because the assistant could not. The question comes from the stored chat
/// (the server reads it, the sender cannot invent it); the sender may add a short note and a contact. Shown only to the company admin.
/// </summary>
public class SupportRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    /// <summary>Where it came from: the share link's name, or "Signed-in user".</summary>
    public string Channel { get; set; } = "";
    /// <summary>The question as the user asked it.</summary>
    public string Question { get; set; } = "";
    /// <summary>True when the assistant did give a documented answer and the user still asked for help.</summary>
    public bool WasAnswered { get; set; }
    /// <summary>What the user was trying to do (optional, written by the user).</summary>
    public string? Note { get; set; }
    /// <summary>How to reach them (optional, written by the user): a name, phone number or e-mail.</summary>
    public string? Contact { get; set; }
    /// <summary>Short hash of who sent it, only to cap how many one visitor can send per day. Not reversible.</summary>
    public string SenderHash { get; set; } = "";
    /// <summary>"open" or "done".</summary>
    public string Status { get; set; } = "open";
    /// <summary>The admin's own note on how it was handled (optional).</summary>
    public string? Resolution { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }
}

/// <summary>
/// Where a company keeps its uploaded files. "local" = this server's storage folder (default). "r2" and "s3" = an S3-compatible bucket
/// (Cloudflare R2, or any S3-compatible service such as MinIO or the company's own cloud). The access keys are stored encrypted with the
/// server secret and never returned to the browser. The connection details stay saved even when the company switches back to "local",
/// because files already copied to the bucket are still read from it.
/// </summary>
public class StorageSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    /// <summary>The location NEW uploads go to: "local", "r2", "s3" or "folder".</summary>
    public string Provider { get; set; } = "local";
    /// <summary>Folder on a server the app can reach (a drive on the IIS machine, or a file share). Must lie inside an operator-approved root. Kept when another location is chosen, so older files still open.</summary>
    public string FolderPath { get; set; } = "";
    /// <summary>"r2" or "s3" - how the connection below is described to the admin (r2 builds the address from the Cloudflare account id).</summary>
    public string RemoteType { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "auto";
    /// <summary>Optional folder inside the bucket.</summary>
    public string Prefix { get; set; } = "";
    public string? AccessKeyCipher { get; set; }
    public string? SecretKeyCipher { get; set; }
    /// <summary>Last 4 characters of the access key id, so the admin can recognise which key is saved.</summary>
    public string? AccessKeyHint { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastTestUtc { get; set; }
    public bool? LastTestOk { get; set; }
    public string? LastTestMessage { get; set; }
}

/// <summary>
/// A folder location the platform administrator approved for "Folder on a server". Companies may only choose a folder inside one of these
/// (or inside a root set in the server configuration). Not tenant-owned: it is a decision about the server, not about one company.
/// </summary>
public class AllowedFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Full, normalised path of the approved location.</summary>
    public string Path { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
