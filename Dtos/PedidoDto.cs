using System;
using System.Collections.Generic;

namespace EsaLogistica.Api.Dtos
{
    public class PedidoDto
    {
        public string almacenCodigo { get; set; } = "";
        public string almacenEmplazamientoCodigo { get; set; } = "";
        public string tipoCodigo { get; set; } = "";
        public string categoria { get; set; } = "";
        public string sucursal { get; set; } = "";
        public string numero { get; set; } = "";
        public DateTime fechaEmision { get; set; }
        public DateTime fechaEntrega { get; set; }
        public string clienteCodigo { get; set; } = "";
        public string? subClienteCodigo { get; set; }
        public string razonSocial { get; set; } = "";
        public string domicilio { get; set; } = "";
        public string? localidadCodigo { get; set; }
        public string? localidadNombre { get; set; }
        public string? codigoPostal { get; set; }
        public string? provincia { get; set; }
        public decimal importeFactura { get; set; }
        public int prioridad { get; set; }
        public string? referenciaA { get; set; }
        public string? referenciaB { get; set; }
        public string? observaciones { get; set; }
        public List<PedidoDetalleDto> detalle { get; set; } = new();
    }
}
