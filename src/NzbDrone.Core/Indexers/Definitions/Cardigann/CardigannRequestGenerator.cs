using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Indexers.Cardigann
{
    public class CardigannRequestGenerator : CardigannBase, IIndexerRequestGenerator
    {
        private List<string> _defaultCategories = new List<string>();

        public CardigannRequestGenerator(CardigannDefinition definition,
                                         CardigannSettings settings,
                                         Logger logger)
        : base(definition, settings, logger)
        {
        }

        public Func<IDictionary<string, string>> GetCookies { get; set; }
        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }

        public virtual IndexerPageableRequestChain GetRecentRequests()
        {
            _logger.Trace("Getting recent");

            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetRequest(null));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(MovieSearchCriteria searchCriteria)
        {
            _logger.Trace("Getting search");

            var pageableRequests = new IndexerPageableRequestChain();

            foreach (var queryTitle in searchCriteria.QueryTitles)
            {
                pageableRequests.Add(GetRequest(string.Format("{0}", queryTitle)));
            }

            return pageableRequests;
        }

        private IEnumerable<IndexerRequest> GetRequest(string searchCriteria)
        {
            var search = _definition.Search;

            // init template context
            var variables = GetBaseTemplateVariables();

            variables[".Query.Type"] = null;
            variables[".Query.Q"] = searchCriteria;
            variables[".Query.Series"] = null;
            variables[".Query.Ep"] = null;
            variables[".Query.Season"] = null;
            variables[".Query.Movie"] = null;
            variables[".Query.Year"] = null;
            variables[".Query.Limit"] = null;
            variables[".Query.Offset"] = null;
            variables[".Query.Extended"] = null;
            variables[".Query.Categories"] = null;
            variables[".Query.APIKey"] = null;
            variables[".Query.TVDBID"] = null;
            variables[".Query.TVRageID"] = null;
            variables[".Query.IMDBID"] = null;
            variables[".Query.IMDBIDShort"] = null;
            variables[".Query.TMDBID"] = null;
            variables[".Query.TVMazeID"] = null;
            variables[".Query.TraktID"] = null;
            variables[".Query.Album"] = null;
            variables[".Query.Artist"] = null;
            variables[".Query.Label"] = null;
            variables[".Query.Track"] = null;
            variables[".Query.Episode"] = null;
            variables[".Query.Author"] = null;
            variables[".Query.Title"] = null;

            /*
            var mappedCategories = MapTorznabCapsToTrackers(query);
            if (mappedCategories.Count == 0)
            {
                mappedCategories = _defaultCategories;
            }
            */

            var mappedCategories = _defaultCategories;

            variables[".Categories"] = mappedCategories;

            var keywordTokens = new List<string>();
            var keywordTokenKeys = new List<string> { "Q", "Series", "Movie", "Year" };
            foreach (var key in keywordTokenKeys)
            {
                var value = (string)variables[".Query." + key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    keywordTokens.Add(value);
                }
            }

            if (!string.IsNullOrWhiteSpace((string)variables[".Query.Episode"]))
            {
                keywordTokens.Add((string)variables[".Query.Episode"]);
            }

            variables[".Query.Keywords"] = string.Join(" ", keywordTokens);
            variables[".Keywords"] = ApplyFilters((string)variables[".Query.Keywords"], search.Keywordsfilters);

            // TODO: prepare queries first and then send them parallel
            var searchPaths = search.Paths;
            foreach (var searchPath in searchPaths)
            {
                // skip path if categories don't match
                if (searchPath.Categories != null && mappedCategories.Count > 0)
                {
                    var invertMatch = searchPath.Categories[0] == "!";
                    var hasIntersect = mappedCategories.Intersect(searchPath.Categories).Any();
                    if (invertMatch)
                    {
                        hasIntersect = !hasIntersect;
                    }

                    if (!hasIntersect)
                    {
                        continue;
                    }
                }

                // build search URL
                // HttpUtility.UrlPathEncode seems to only encode spaces, we use UrlEncode and replace + with %20 as a workaround
                var searchUrl = ResolvePath(ApplyGoTemplateText(searchPath.Path, variables, WebUtility.UrlEncode).Replace("+", "%20")).AbsoluteUri;
                var queryCollection = new List<KeyValuePair<string, string>>();
                var method = HttpMethod.GET;

                if (string.Equals(searchPath.Method, "post", StringComparison.OrdinalIgnoreCase))
                {
                    method = HttpMethod.POST;
                }

                var inputsList = new List<Dictionary<string, string>>();
                if (searchPath.Inheritinputs)
                {
                    inputsList.Add(search.Inputs);
                }

                inputsList.Add(searchPath.Inputs);

                foreach (var inputs in inputsList)
                {
                    if (inputs != null)
                    {
                        foreach (var input in inputs)
                        {
                            if (input.Key == "$raw")
                            {
                                var rawStr = ApplyGoTemplateText(input.Value, variables, WebUtility.UrlEncode);
                                foreach (var part in rawStr.Split('&'))
                                {
                                    var parts = part.Split(new char[] { '=' }, 2);
                                    var key = parts[0];
                                    if (key.Length == 0)
                                    {
                                        continue;
                                    }

                                    var value = "";
                                    if (parts.Length == 2)
                                    {
                                        value = parts[1];
                                    }

                                    queryCollection.Add(key, value);
                                }
                            }
                            else
                            {
                                queryCollection.Add(input.Key, ApplyGoTemplateText(input.Value, variables));
                            }
                        }
                    }
                }

                if (method == HttpMethod.GET)
                {
                    if (queryCollection.Count > 0)
                    {
                        searchUrl += "?" + queryCollection.GetQueryString(_encoding);
                    }
                }

                _logger.Info($"Adding request: {searchUrl}");

                var request = new CardigannRequest(searchUrl, HttpAccept.Html, variables);

                // send HTTP request
                if (search.Headers != null)
                {
                    foreach (var header in search.Headers)
                    {
                        request.HttpRequest.Headers.Add(header.Key, header.Value[0]);
                    }
                }

                request.HttpRequest.Method = method;

                yield return request;
            }
        }
    }
}
