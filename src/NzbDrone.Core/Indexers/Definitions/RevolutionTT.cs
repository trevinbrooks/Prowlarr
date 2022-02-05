using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AngleSharp.Html.Parser;
using FluentValidation;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.IndexerVersions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Definitions
{
    public class RevolutionTT : TorrentIndexerBase<RevolutionTTSettings>
    {
        public override string Name => "RevolutionTT";

        public override string[] IndexerUrls => new string[] { "https://revolutiontt.me/" };
        public override string Description => "The Revolution has begun";
        private string LoginUrl => Settings.BaseUrl + "takelogin.php";
        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override IndexerPrivacy Privacy => IndexerPrivacy.Private;

        public RevolutionTT(IIndexerHttpClient httpClient, IEventAggregator eventAggregator, IIndexerStatusService indexerStatusService, IIndexerDefinitionUpdateService definitionService, IConfigService configService, Logger logger)
            : base(httpClient, eventAggregator, indexerStatusService, definitionService, configService, logger)
        {
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new RevolutionTTRequestGenerator() { Settings = Settings, Capabilities = Capabilities };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new RevolutionTTParser(Settings, Capabilities.Categories);
        }

        protected override async Task DoLogin()
        {
            UpdateCookies(null, null);

            var requestBuilder = new HttpRequestBuilder(LoginUrl)
            {
                LogResponseContent = true,
                AllowAutoRedirect = true
            };

            var loginPage = await ExecuteAuth(new HttpRequest(Settings.BaseUrl + "login.php"));

            requestBuilder.Method = HttpMethod.Post;
            requestBuilder.PostProcess += r => r.RequestTimeout = TimeSpan.FromSeconds(15);
            requestBuilder.SetCookies(loginPage.GetCookies());

            var authLoginRequest = requestBuilder
                .AddFormParameter("username", Settings.Username)
                .AddFormParameter("password", Settings.Password)
                .SetHeader("Content-Type", "multipart/form-data")
                .Build();

            var response = await ExecuteAuth(authLoginRequest);

            if (response.Content != null && response.Content.Contains("/logout.php"))
            {
                UpdateCookies(response.GetCookies(), DateTime.Now + TimeSpan.FromDays(30));

                _logger.Debug("RevolutionTT authentication succeeded");
            }
            else
            {
                throw new IndexerAuthException("RevolutionTT authentication failed");
            }
        }

        protected override bool CheckIfLoginNeeded(HttpResponse httpResponse)
        {
            if (httpResponse.HasHttpRedirect || !httpResponse.Content.Contains("/logout.php"))
            {
                return true;
            }

            return false;
        }
    }

    public class RevolutionTTRequestGenerator : IIndexerRequestGenerator
    {
        public RevolutionTTSettings Settings { get; set; }
        public IndexerCapabilities Capabilities { get; set; }

        public RevolutionTTRequestGenerator()
        {
        }

        private IEnumerable<IndexerRequest> GetPagedRequests(string term, int[] categories, string imdbId = null)
        {
            var qc = new NameValueCollection
            {
                { "incldead", "1" }
            };

            if (imdbId.IsNotNullOrWhiteSpace())
            {
                qc.Add("titleonly", "0");
                qc.Add("search", imdbId);
            }
            else
            {
                qc.Add("titleonly", "1");
                qc.Add("search", term);
            }

            var cats = Capabilities.Categories.MapTorznabCapsToTrackers(categories);

            if (cats.Count > 0)
            {
                foreach (var cat in cats)
                {
                    qc.Add($"c{cat}", "1");
                }
            }

            var searchUrl = Settings.BaseUrl + "browse.php?" + qc.GetQueryString();

            var request = new IndexerRequest(searchUrl, HttpAccept.Html);

            yield return request;
        }

        public IndexerPageableRequestChain GetSearchRequests(MovieSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetPagedRequests(string.Format("{0}", searchCriteria.SanitizedSearchTerm), searchCriteria.Categories, searchCriteria.FullImdbId));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(MusicSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetPagedRequests(string.Format("{0}", searchCriteria.SanitizedSearchTerm), searchCriteria.Categories));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(TvSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetPagedRequests(string.Format("{0}", searchCriteria.SanitizedTvSearchString), searchCriteria.Categories, searchCriteria.FullImdbId));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(BookSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetPagedRequests(string.Format("{0}", searchCriteria.SanitizedSearchTerm), searchCriteria.Categories));

            return pageableRequests;
        }

        public IndexerPageableRequestChain GetSearchRequests(BasicSearchCriteria searchCriteria)
        {
            var pageableRequests = new IndexerPageableRequestChain();

            pageableRequests.Add(GetPagedRequests(string.Format("{0}", searchCriteria.SanitizedSearchTerm), searchCriteria.Categories));

            return pageableRequests;
        }

        public Func<IDictionary<string, string>> GetCookies { get; set; }
        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }
    }

    public class RevolutionTTParser : IParseIndexerResponse
    {
        private readonly RevolutionTTSettings _settings;
        private readonly IndexerCapabilitiesCategories _categories;

        public RevolutionTTParser(RevolutionTTSettings settings, IndexerCapabilitiesCategories categories)
        {
            _settings = settings;
            _categories = categories;
        }

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            var torrentInfos = new List<TorrentInfo>();

            var parser = new HtmlParser();
            var dom = parser.ParseDocument(indexerResponse.Content);
            var rows = dom.QuerySelectorAll("#torrents-table > tbody > tr");

            foreach (var row in rows.Skip(1))
            {
                var qDetails = row.QuerySelector(".br_right > a");
                var details = _settings.BaseUrl + qDetails.GetAttribute("href");
                var title = qDetails.QuerySelector("b").TextContent;

                // Remove auto-generated [REQ] tag from fulfilled requests
                if (title.StartsWith("[REQ] "))
                {
                    title = title.Substring(6);
                }

                var qLink = row.QuerySelector("td:nth-child(4) > a");
                if (qLink == null)
                {
                    continue; // support/donation banner
                }

                var link = _settings.BaseUrl + qLink.GetAttribute("href");

                // dateString format "yyyy-MMM-dd hh:mm:ss" => eg "2015-04-25 23:38:12"
                var dateString = row.QuerySelector("td:nth-child(6) nobr").TextContent.Trim();
                var publishDate = DateTime.ParseExact(dateString, "yyyy-MM-ddHH:mm:ss", CultureInfo.InvariantCulture);

                var size = ParseUtil.GetBytes(row.QuerySelector("td:nth-child(7)").InnerHtml.Split('<').First().Trim());
                var files = ParseUtil.GetLongFromString(row.QuerySelector("td:nth-child(7) > a").TextContent);
                var grabs = ParseUtil.GetLongFromString(row.QuerySelector("td:nth-child(8)").TextContent);
                var seeders = ParseUtil.CoerceInt(row.QuerySelector("td:nth-child(9)").TextContent);
                var leechers = ParseUtil.CoerceInt(row.QuerySelector("td:nth-child(10)").TextContent);

                var category = row.QuerySelector(".br_type > a").GetAttribute("href").Replace("browse.php?cat=", string.Empty);

                var qImdb = row.QuerySelector("a[href*=\"www.imdb.com/\"]");
                var imdb = qImdb != null ? ParseUtil.GetImdbID(qImdb.GetAttribute("href").Split('/').Last()) : null;

                var release = new TorrentInfo
                {
                    InfoUrl = details,
                    Guid = details,
                    Title = title,
                    DownloadUrl = link,
                    PublishDate = publishDate,
                    Size = size,
                    Seeders = seeders,
                    Peers = seeders + leechers,
                    Grabs = (int)grabs,
                    Files = (int)files,
                    Categories = _categories.MapTrackerCatToNewznab(category),
                    ImdbId = imdb ?? 0,
                    MinimumRatio = 1,
                    MinimumSeedTime = 172800, // 48 hours
                    UploadVolumeFactor = 1,
                    DownloadVolumeFactor = 1
                };

                torrentInfos.Add(release);
            }

            return torrentInfos.ToArray();
        }

        public Action<IDictionary<string, string>, DateTime?> CookiesUpdater { get; set; }
    }

    public class RevolutionTTSettingsValidator : AbstractValidator<RevolutionTTSettings>
    {
        public RevolutionTTSettingsValidator()
        {
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Password).NotEmpty();
        }
    }

    public class RevolutionTTSettings : IIndexerSettings
    {
        private static readonly RevolutionTTSettingsValidator Validator = new RevolutionTTSettingsValidator();

        public RevolutionTTSettings()
        {
            Username = "";
            Password = "";
        }

        [FieldDefinition(1, Label = "Base Url", Type = FieldType.Select, SelectOptionsProviderAction = "getUrls", HelpText = "Select which baseurl Prowlarr will use for requests to the site")]
        public string BaseUrl { get; set; }

        [FieldDefinition(2, Label = "Username", HelpText = "Site Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(3, Label = "Password", HelpText = "Site Password", Privacy = PrivacyLevel.Password, Type = FieldType.Password)]
        public string Password { get; set; }

        [FieldDefinition(4)]
        public IndexerBaseSettings BaseSettings { get; set; } = new IndexerBaseSettings();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
