namespace ApcoaFlowApiClient.Models.Promotion;

/// <summary>
/// Response of <c>POST /promotion/validate</c>. On a usable coupon the endpoint returns 2xx with
/// <see cref="Message"/> == "valid" and <see cref="Error"/> == null; on an unusable one it returns a
/// non-2xx (e.g. 422) whose body carries an error <see cref="Message"/> and <see cref="StatusCode"/>
/// (e.g. "INVALID_PROMOTION_CODE" / 5007). Only the markers needed to tell success from failure are
/// modelled; the success body also carries an opaque merged offer/consumer blob.
/// </summary>
public class ValidatePromotionResponse
{
    public string? TransactionId { get; set; }

    /// <summary>"valid" on success; an error marker (e.g. "INVALID_PROMOTION_CODE") on failure.</summary>
    public string? Message { get; set; }

    /// <summary>Internal status code; an error code (e.g. 5007) on failure, absent on success.</summary>
    public int? StatusCode { get; set; }

    public string? Description { get; set; }

    /// <summary>Populated only on failure; null on a successful validation.</summary>
    public string? Error { get; set; }
}
