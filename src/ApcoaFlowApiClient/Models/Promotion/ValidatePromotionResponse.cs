namespace ApcoaFlowApiClient.Models.Promotion;

/// <summary>
/// Response of <c>POST /promotion/validate</c>. On a usable coupon the endpoint returns 2xx; on an
/// unusable one it returns a non-2xx (e.g. 422) whose body carries an error <see cref="Message"/> and
/// <see cref="StatusCode"/> (e.g. "INVALID_PROMOTION_CODE" / 5007). The success body is otherwise an
/// opaque merged/consumer blob, so only the error markers are modelled.
/// </summary>
public class ValidatePromotionResponse
{
    public string? TransactionId { get; set; }

    /// <summary>Error marker on failure (e.g. "INVALID_PROMOTION_CODE"); absent/neutral on success.</summary>
    public string? Message { get; set; }

    /// <summary>Internal status code; an error code (e.g. 5007) on failure.</summary>
    public int? StatusCode { get; set; }

    public string? Description { get; set; }
}
