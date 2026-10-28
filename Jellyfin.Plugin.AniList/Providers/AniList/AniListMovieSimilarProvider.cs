using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniList.Providers.AniList
{
    public class AniListMovieSimilarProvider : AniListSimilarProvider<Movie>
    {
        public AniListMovieSimilarProvider(ILogger<AniListMovieSimilarProvider> logger)
            : base(logger)
        {
        }
    }
}
