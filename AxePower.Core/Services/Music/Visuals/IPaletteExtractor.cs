using System.Threading;
using System.Threading.Tasks;
using AxePower.Core.Models.Music;

namespace AxePower.Core.Services.Music;

public interface IPaletteExtractor
{
    Task<AlbumPalette> ExtractPaletteAsync(string imagePath, CancellationToken ct = default);
}

