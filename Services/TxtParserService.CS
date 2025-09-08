// ------------------------------------------------------------
//  TxtParserService.cs  –  lectura de TXT con clave 8 dígitos
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EsaLogistica.Api.Dtos;

namespace EsaLogistica.Api.Services
{
    public class TxtParserService : ITxtParserService
    {
        public async Task<IEnumerable<PedidoDto>> ParseTxtAsync(Stream cabeceraStream, Stream detalleStream)
        {
            if (cabeceraStream == null) throw new ArgumentNullException(nameof(cabeceraStream));
            if (detalleStream == null) throw new ArgumentNullException(nameof(detalleStream));

           using var cabReader = new StreamReader(cabeceraStream, Encoding.Latin1, leaveOpen: true);
            var cabLines = (await cabReader.ReadToEndAsync())
                           .Replace("\r", "")
                           .Split('\n')
                           .Where(l => !string.IsNullOrWhiteSpace(l))
                           .Skip(1)
                           .ToList();

            using var detReader = new StreamReader(detalleStream, Encoding.Latin1, leaveOpen: true);
            var detLines = (await detReader.ReadToEndAsync())
                           .Replace("\r", "")
                           .Split('\n')
                           .Where(l => !string.IsNullOrWhiteSpace(l))
                           .Skip(1)
                           .ToList();

            var detPorPedido = detLines
                .GroupBy(l => l.Substring(14, 8).Trim().PadLeft(8, '0'))
                .ToDictionary(g => g.Key, g => g.ToList());

            var pedidos = new List<PedidoDto>();
            foreach (var cabLine in cabLines)
            {
                if (cabLine.Length < 50) continue;

                var numPedido = cabLine.Substring(14, 8).Trim().PadLeft(8, '0');
                var fechaEm = DateTime.ParseExact(cabLine.Substring(24, 8), "yyyyMMdd", CultureInfo.InvariantCulture);
                var fechaEntRaw = cabLine.Length >= 191 ? cabLine.Substring(183, 8).Trim() : string.Empty;
                var fechaEnt = string.IsNullOrEmpty(fechaEntRaw)
                    ? fechaEm.AddDays(1)
                    : DateTime.ParseExact(fechaEntRaw, "yyyyMMdd", CultureInfo.InvariantCulture);

                var pedido = new PedidoDto
                {
                    almacenCodigo = cabLine.Substring(3, 3),
                    almacenEmplazamientoCodigo = cabLine.Substring(0, 3),
                    tipoCodigo = cabLine.Substring(6, 3),
                    categoria = cabLine.Substring(9, 1),
                    sucursal = cabLine.Substring(10, 4),
                    numero = numPedido,
                    fechaEmision = fechaEm,
                    fechaEntrega = fechaEnt,
                    clienteCodigo = cabLine.Substring(32, 10),
                    subClienteCodigo = cabLine.Substring(42, 5).Trim(),
                    razonSocial = cabLine.Substring(47, 30).Trim(),
                    domicilio = cabLine.Substring(77, 35).Trim(),
                    localidadCodigo = cabLine.Length >= 182 ? cabLine.Substring(172, 10).Trim() : string.Empty,
                    provincia = cabLine.Length >= 172 ? cabLine.Substring(142, 30).Trim() : string.Empty,
                    importeFactura = cabLine.Length >= 271 ? decimal.Parse(cabLine.Substring(251, 20), CultureInfo.InvariantCulture) / 100 : 0m,
                    prioridad = 1,
                    referenciaA = cabLine.Length >= 221 ? cabLine.Substring(191, 30).Trim() : string.Empty,
                    referenciaB = cabLine.Length >= 251 ? cabLine.Substring(221, 30).Trim() : string.Empty,
                    observaciones = cabLine.Length > 308 ? cabLine.Substring(308).Trim() : string.Empty,
                    detalle = new List<PedidoDetalleDto>()
                };

                if (detPorPedido.TryGetValue(numPedido, out var dets))
                {
                    int idx = 0;
                    foreach (var det in dets)
                    {
                        if (det.Length < 65) continue;
                        var compania = det.Substring(34, 3).Trim();
                        var producto = det.Substring(37, 15).Trim();
                        var qtyRaw = det.Substring(52, 13).Trim();
                        int cantVal = 0;
                        if (!string.IsNullOrEmpty(qtyRaw) && long.TryParse(qtyRaw, out var q))
                            cantVal = (int)(q / 100);

                        pedido.detalle.Add(new PedidoDetalleDto
                        {
                            linea = idx++,
                            productoCompaniaCodigo = compania,
                            productoCodigo = producto,
                            loteCodigo = string.Empty,
                            serie = string.Empty,
                            cantidad = cantVal,
                            despachoParcial = false
                        });
                    }
                }
                pedidos.Add(pedido);
            }
            return pedidos;
        }
   }
}
