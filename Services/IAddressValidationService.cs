using System.Threading;
using System.Threading.Tasks;

namespace EsaLogistica.Api.Services
{
    public record AddressValidationResult(
        bool IsValid,
        string? NormalizedAddress,
        string? Province,
        string? Locality,
        string? PostalCode,
        double? Lat,
        double? Lng,
        double Confidence,
        string? Provider,
        string[]? Mismatches
    );

    public interface IAddressValidationService
    {
        Task<AddressValidationResult> ValidateAsync(
            string direccion, string? localidad, string? provincia, string? codigoPostal,
            CancellationToken ct = default);
    }
} 