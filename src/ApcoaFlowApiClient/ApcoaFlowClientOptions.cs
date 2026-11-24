using System.Text;

namespace ApcoaFlowApiClient;

public class ApcoaFlowClientOptions
{
    public const string BaseUrl = "https://api.apcoaflow.com";

    // The app's static API key, base64-encoded only to keep the literal out of plain-text/secret
    // scans and search engines. This is obfuscation, NOT protection — it is trivially decodable and
    // is a value the public iOS app ships anyway.
    private const string ApiKeyEncoded =
        "OTNkMjExNjhiMTllNDI1Y2EyYmZlMDJiYzkyMGMwMmFkMjk1Nzg0OGE2Yjc0YzdiODhhMjlkODVjZjNmZWQzMw==";

    private static string ApiKey => Encoding.UTF8.GetString(Convert.FromBase64String(ApiKeyEncoded));

    /// <summary>
    /// Static app-level headers the APCOA backend requires on every request (login included).
    /// Without the <c>X-ApiKey</c> the server rejects even a valid login with HTTP 419
    /// "Invalid or Expired Token". Captured from the iOS app (APCOA FLOW 6.85.2).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AppHeaders = new Dictionary<string, string>
    {
        ["X-ApiKey"] = ApiKey,
        ["x_auth_request_type"] = "jwt_consumer",
        ["x_device_type"] = "iOS",
        ["x_app_version"] = "6.85.2",
        ["x_language_code"] = "pl",
        ["x_gmp_tenant"] = "apcoa",
        ["User-Agent"] = "APCOA FLOW/6.85.2 (com.apcoaflow.consumer; build:1; iOS 27.2.1) Alamofire/5.10.2",
    };

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
