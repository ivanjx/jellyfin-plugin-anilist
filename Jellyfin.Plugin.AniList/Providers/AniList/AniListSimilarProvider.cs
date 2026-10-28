using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniList.Providers.AniList
{
    /// <summary>
    /// Feeds the "More Like This" section with AniList user recommendations.
    /// Jellyfin resolves the returned AniList ids against the local library.
    /// </summary>
    public abstract class AniListSimilarProvider<TItemType> : IRemoteSimilarItemsProvider<TItemType>
        where TItemType : BaseItem
    {
        private readonly ILogger _log;
        private readonly AniListApi _aniListApi;

        protected AniListSimilarProvider(ILogger logger)
        {
            _log = logger;
            _aniListApi = new AniListApi(logger);
        }

        public string Name => ProviderNames.AniList;

        public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;

        public TimeSpan? CacheDuration
        {
            get
            {
                var days = Plugin.Instance?.Configuration.SimilarItemsCacheDays ?? 0;
                return days > 0 ? TimeSpan.FromDays(days) : null;
            }
        }

        public async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(
            TItemType item,
            SimilarItemsQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!item.TryGetProviderId(ProviderNames.AniList, out var aid))
            {
                yield break;
            }

            var page = 1;
            var hasNextPage = true;

            while (hasNextPage && !cancellationToken.IsCancellationRequested)
            {
                RecommendationConnection recommendations;
                try
                {
                    recommendations = await _aniListApi.GetRecommendations(aid, page, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Failed to get recommendations from AniList for {AniListId} page {Page}", aid, page);
                    yield break;
                }

                if (recommendations?.nodes is null || recommendations.nodes.Count == 0)
                {
                    yield break;
                }

                foreach (var recommendation in recommendations.nodes)
                {
                    // Recommendations whose target entry was removed from AniList come back without media
                    if (recommendation.mediaRecommendation is null)
                    {
                        continue;
                    }

                    yield return new SimilarItemReference
                    {
                        ProviderName = ProviderNames.AniList,
                        ProviderId = recommendation.mediaRecommendation.id.ToString(CultureInfo.InvariantCulture)
                    };
                }

                hasNextPage = recommendations.pageInfo?.hasNextPage ?? false;
                page++;
            }
        }
    }
}
