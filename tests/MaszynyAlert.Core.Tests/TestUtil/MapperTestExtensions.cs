using MaszynyAlert.Core.Ocr;

namespace MaszynyAlert.Core.Tests.TestUtil;

public static class MapperTestExtensions
{
    public static MappedMessage MapAidite(this MessageMapper mapper, string? ocrText) =>
        mapper.Map(ocrText, TestPaths.AiditeProfile);
}
