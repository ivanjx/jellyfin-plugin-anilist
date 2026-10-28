using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniList.Providers.AniList
{
    public class AniListSeriesSimilarProvider : AniListSimilarProvider<Series>
    {
        public AniListSeriesSimilarProvider(ILogger<AniListSeriesSimilarProvider> logger)
            : base(logger)
        {
        }
    }
}
