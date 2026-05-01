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
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Microsoft.Extensions.Logging;

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
        private static readonly SemaphoreSlim _rateLimitLock = new(1, 1);
        private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
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
            _logger = logger;
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

            return result.data?.Media;
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

            return result.data.Page.media;
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

            return result.data?.Staff;
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

            return result.data?.Page.staff;
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
            string responseBody = null;
            bool success = false;
            var requestsPerMinute = Plugin.Instance.Configuration.AniDbRateLimit;
            var delayBetweenRequests = requestsPerMinute > 0 ?
                TimeSpan.FromMinutes(1d / requestsPerMinute) :
                TimeSpan.Zero;

            await _rateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (delayBetweenRequests > TimeSpan.Zero &&
                        _lastRequestAt > DateTimeOffset.MinValue)
                    {
                        var rateLimitDelay = delayBetweenRequests - (DateTimeOffset.UtcNow - _lastRequestAt);
                        if (rateLimitDelay > TimeSpan.Zero)
                        {
                            _logger?.LogInformation("Waiting {Delay} ms for rate limit.", rateLimitDelay.TotalMilliseconds);
                            await Task.Delay(rateLimitDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    _lastRequestAt = DateTimeOffset.UtcNow;
                    using HttpContent content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                    using var response = await httpClient.PostAsync(BaseApiUrl, content, cancellationToken).ConfigureAwait(false);
                    responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    success = response.IsSuccessStatusCode;

                    if (success ||
                        response.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        break;
                    }

                    // Got HTTP 429 response
                    var delay = response.Headers.RetryAfter?.Delta;
                    if (delay is null &&
                        response.Headers.RetryAfter?.Date is { } retryAfterDate)
                    {
                        delay = retryAfterDate - DateTimeOffset.UtcNow;
                    }

                    var retryDelay = delay.GetValueOrDefault(TimeSpan.FromSeconds(60));
                    if (retryDelay <= TimeSpan.Zero)
                    {
                        // If the Retry-After header is missing or invalid, default to a 60 second delay
                        retryDelay = TimeSpan.FromSeconds(60);
                    }

                    _logger?.LogInformation("Rate limited by AniList API. Retrying after {RetryDelay} ms.", retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _rateLimitLock.Release();
            }

            if (!success)
            {
                return new RootObject();
            }

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return new RootObject();
            }

            RootObject result = JsonSerializer.Deserialize<RootObject>(responseBody);

            if (result is null)
            {
                return new RootObject();
            }

            return result;
        }
    }
}
