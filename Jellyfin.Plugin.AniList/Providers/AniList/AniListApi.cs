using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Retry;

namespace Jellyfin.Plugin.AniList.Providers.AniList
{
    /// <summary>
    /// Based on the new API from AniList
    /// 🛈 This code works with the API Interface (v2) from AniList
    /// 🛈 https://anilist.gitbook.io/anilist-apiv2-docs
    /// 🛈 THIS IS AN UNOFFICAL API INTERFACE FOR JELLYFIN
    /// </summary>
    public class AniListApi
    {
        private const string BaseApiUrl = "https://graphql.anilist.co/";
        private static readonly object _rateLimiterLock = new();
        private static TokenBucketRateLimiter _rateLimiter;
        private static int _rateLimiterRequestsPerMinute;
        private readonly ILogger _logger;

        private const string SearchAnimeGraphqlQuery = """
            query ($query: String) {
              Page {
                media(search: $query, type: ANIME) {
                  id
                  title {
                    romaji
                    english
                    native
                  }
                  coverImage {
                    medium
                    large
                    extraLarge
                  }
                  startDate {
                    year
                    month
                    day
                  }
                }
              }
            }
        """;

        private const string GetAnimeGraphqlQuery = """
            query($id: Int!) {
              Media(id: $id, type: ANIME) {
                id
                title {
                  romaji
                  english
                  native
                  userPreferred
                }
                startDate {
                  year
                  month
                  day
                }
                endDate {
                  year
                  month
                  day
                }
                coverImage {
                  medium
                  large
                  extraLarge
                }
                bannerImage
                format
                type
                status
                episodes
                chapters
                volumes
                season
                seasonYear
                description
                averageScore
                meanScore
                genres
                synonyms
                duration
                tags {
                  id
                  name
                  rank
                  category
                  isMediaSpoiler
                }
                nextAiringEpisode {
                  airingAt
                  timeUntilAiring
                  episode
                }

                studios {
                  edges {
                    node {
                      id
                      name
                      isAnimationStudio
                    }
                    isMain
                  }
                }
                characters(sort: [ROLE, FAVOURITES_DESC]) {
                  edges {
                    node {
                      id
                      name {
                        first
                        last
                        full
                      }
                      image {
                        medium
                        large
                      }
                    }
                    role
                    voiceActors {
                      id
                      name {
                        first
                        last
                        full
                        native
                      }
                      image {
                        medium
                        large
                      }
                      language: languageV2
                    }
                  }
                }
              }
            }
        """;

        private const string SearchStaffGraphqlQuery = """
            query($query: String) {
              Page {
                staff(search: $query) {
                  id
                  name {
                    first
                    last
                    full
                    native
                  }
                  image {
                    large
                    medium
                  }
                }
              }
            }
        """;

        private const string GetStaffGraphqlQuery = """
            query($id: Int!) {
              Staff(id: $id) {
                id
                name {
                  first
                  last
                  full
                  native
                }
                image {
                  large
                  medium
                }
                description(asHtml: true)
                homeTown
                dateOfBirth {
                  year
                  month
                  day
                }
                dateOfDeath {
                  year
                  month
                  day
                }
              }
            }
        """;

        private class GraphQlRequest {
            [JsonPropertyName("query")]
            public string Query { get; set; }

            [JsonPropertyName("variables")]
            public Dictionary<string, string> Variables { get; set; }
        }

        public AniListApi(ILogger logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// API call to get the anime with the given id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public async Task<Media> GetAnime(string id, CancellationToken cancellationToken)
        {
            RootObject result = await WebRequestAPI(
                new GraphQlRequest {
                    Query = GetAnimeGraphqlQuery,
                    Variables = new Dictionary<string, string> {{"id", id}},
                },
                cancellationToken
            ).ConfigureAwait(false);

            if (result?.data?.Media is null)
            {
                _logger?.LogError("AniList returned no media payload for id {Id}.", id);
            }

            return result?.data?.Media;
        }

        /// <summary>
        /// API call to search a title and return the first result
        /// </summary>
        /// <param name="title"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<MediaSearchResult> Search_GetSeries(string title, CancellationToken cancellationToken)
        {
            return (await Search_GetSeries_list(title, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        }

        /// <summary>
        /// API call to search a title and return a list of results
        /// </summary>
        /// <param name="title"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<List<MediaSearchResult>> Search_GetSeries_list(string title, CancellationToken cancellationToken)
        {
            RootObject result = await WebRequestAPI(
                new GraphQlRequest {
                    Query = SearchAnimeGraphqlQuery,
                    Variables = new Dictionary<string, string> {{"query", title}},
                },
                cancellationToken
            ).ConfigureAwait(false);

            if (result?.data?.Page?.media is null)
            {
                _logger?.LogError("AniList search response contained no media list for query {Title}.", title);
            }

            return result?.data?.Page?.media ?? [];
        }

        /// <summary>
        /// Search for anime with the given title. Attempts to fuzzy search by removing special characters
        /// </summary>
        /// <param name="title"></param>
        /// <returns></returns>
        public async Task<string> FindSeries(string title, CancellationToken cancellationToken)
        {
            MediaSearchResult result = await Search_GetSeries(title, cancellationToken);
            if (result is not null)
            {
                return result.id.ToString(CultureInfo.InvariantCulture);
            }

            result = await Search_GetSeries(await Equals_check.Clear_name(title, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result.id.ToString(CultureInfo.InvariantCulture);
            }

            return null;
        }

        public async Task<Staff> GetStaff(int id, CancellationToken cancellationToken)
        {
            RootObject result = await WebRequestAPI(
                new GraphQlRequest {
                    Query = GetStaffGraphqlQuery,
                    Variables = new Dictionary<string, string> {{"id", id.ToString(CultureInfo.InvariantCulture)}},
                },
                cancellationToken
            ).ConfigureAwait(false);

            if (result?.data?.Staff is null)
            {
                _logger?.LogError("AniList returned no staff payload for id {Id}.", id);
            }

            return result?.data?.Staff;
        }

        public async Task<List<Staff>> SearchStaff(string query, CancellationToken cancellationToken)
        {
            RootObject result = await WebRequestAPI(
                new GraphQlRequest {
                    Query = SearchStaffGraphqlQuery,
                    Variables = new Dictionary<string, string> {{"query", query}},
                },
                cancellationToken
            ).ConfigureAwait(false);

            if (result?.data?.Page?.staff is null)
            {
                _logger?.LogWarning("AniList search response contained no staff list for query {Query}.", query);
            }

            return result?.data?.Page?.staff ?? [];
        }

        /// <summary>
        /// Send a GraphQL request, deserialize into a RootObject
        /// </summary>
        /// <param name="request">The GraphQl request payload</param>
        /// <returns></returns>
        private async Task<RootObject> WebRequestAPI(GraphQlRequest request, CancellationToken cancellationToken)
        {
            var httpClient = Plugin.Instance.GetHttpClient();
            var requestBody = JsonSerializer.Serialize(request);
            using var response = await CreatePipeline().ExecuteAsync(
                async ct =>
                {
                    using HttpContent content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    return await httpClient.PostAsync(BaseApiUrl, content, ct).ConfigureAwait(false);
                },
                cancellationToken
            ).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Failed to make request to AniList API after retrying due to rate limits. Giving up.");
                return null;
            }

            using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<RootObject>(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private ResiliencePipeline<HttpResponseMessage> CreatePipeline()
        {
            var builder = new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    // Handle HTTP 429 once
                    ShouldHandle = new PredicateBuilder<HttpResponseMessage>().HandleResult(response =>
                        response.StatusCode == HttpStatusCode.TooManyRequests),
                    MaxRetryAttempts = 1,
                    DelayGenerator = args => new ValueTask<TimeSpan?>(GetRetryDelay(args.Outcome.Result)),
                    OnRetry = args =>
                    {
                        args.Outcome.Result?.Dispose();
                        _logger.LogInformation("Rate limited by AniList API. Retrying after {RetryDelay} ms.", args.RetryDelay.TotalMilliseconds);
                        return default;
                    },
                });

            var rateLimiter = GetRateLimiter();
            if (rateLimiter is not null)
            {
                builder.AddRateLimiter(rateLimiter);
            }

            return builder.Build();
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage response)
        {
            var delay = response.Headers.RetryAfter?.Delta;
            if (delay is null &&
                response.Headers.RetryAfter?.Date is { } retryAfterDate)
            {
                // Use the Retry-After date header
                delay = retryAfterDate - DateTimeOffset.UtcNow;
            }

            var retryDelay = delay ?? TimeSpan.Zero;
            if (retryDelay <= TimeSpan.Zero)
            {
                // Default to 60 seconds if the header is missing
                retryDelay = TimeSpan.FromSeconds(60);
            }

            return retryDelay;
        }

        private static TokenBucketRateLimiter GetRateLimiter()
        {
            var requestsPerMinute = Plugin.Instance.Configuration.AniDbRateLimit;
            if (requestsPerMinute <= 0)
            {
                // Rate limiting disabled
                return null;
            }

            lock (_rateLimiterLock)
            {
                if (_rateLimiter is not null &&
                    _rateLimiterRequestsPerMinute == requestsPerMinute)
                {
                    return _rateLimiter;
                }

                // Rate limit configuration has changed, create a new rate limiter
                _rateLimiterRequestsPerMinute = requestsPerMinute;
                _rateLimiter = CreateRateLimiter(requestsPerMinute);
                return _rateLimiter;
            }
        }

        private static TokenBucketRateLimiter CreateRateLimiter(int requestsPerMinute)
        {
            return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                // Evenly spaced requests instead of bursty
                AutoReplenishment = true,
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1d / requestsPerMinute),
                TokenLimit = 1,
                TokensPerPeriod = 1,
            });
        }
    }
}
