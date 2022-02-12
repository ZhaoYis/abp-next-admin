﻿using LINGYUN.Abp.Location.Tencent.Response;
using LINGYUN.Abp.Location.Tencent.Utils;
using LINGYUN.Abp.Location.Tencent.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Json;
using Volo.Abp.Threading;

namespace LINGYUN.Abp.Location.Tencent
{
    public class TencentLocationHttpClient : ITransientDependency
    {
        protected TencentLocationOptions Options { get; }
        protected IJsonSerializer JsonSerializer { get; }
        protected IServiceProvider ServiceProvider { get; }
        protected IHttpClientFactory HttpClientFactory { get; }
        protected ICancellationTokenProvider CancellationTokenProvider { get; }
        private const string TencentMapApiUrl = "https://apis.map.qq.com";

        public TencentLocationHttpClient(
            IOptions<TencentLocationOptions> options,
            IJsonSerializer jsonSerializer,
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory,
            ICancellationTokenProvider cancellationTokenProvider)
        {
            Options = options.Value;
            JsonSerializer = jsonSerializer;
            ServiceProvider = serviceProvider;
            HttpClientFactory = httpClientFactory;
            CancellationTokenProvider = cancellationTokenProvider;
        }

        public virtual async Task<IPGecodeLocation> IPGeocodeAsync(string ipAddress)
        {
            var requestParameters = new Dictionary<string, string>
            {
                { "callback", Options.Callback },
                { "ip", ipAddress },
                { "key", Options.AccessKey },
                { "output", Options.Output }
            };
            var tencentMapPath = "/ws/location/v1/ip";
            if (!Options.SecretKey.IsNullOrWhiteSpace())
            {
                var sig = TencentSecretKeyCaculater.CalcSecretKey(tencentMapPath, Options.SecretKey, requestParameters);
                requestParameters.Add("sig", sig);
            }
            var tencentLocationResponse = await GetTencentMapResponseAsync<TencentIPGeocodeResponse>(TencentMapApiUrl, tencentMapPath, requestParameters);

            var location = new IPGecodeLocation
            {
                IpAddress = tencentLocationResponse.Result.IpAddress,
                AdCode = tencentLocationResponse.Result.AddressInfo.AdCode,
                City = tencentLocationResponse.Result.AddressInfo.City,
                Country = tencentLocationResponse.Result.AddressInfo.Nation,
                District = tencentLocationResponse.Result.AddressInfo.District,
                Location = new Location
                {
                    Latitude = tencentLocationResponse.Result.Location.Lat,
                    Longitude = tencentLocationResponse.Result.Location.Lng
                },
                Province = tencentLocationResponse.Result.AddressInfo.Province
            };
            location.AddAdditional("TencentLocation", tencentLocationResponse.Result);

            return location;
        }

        public virtual async Task<GecodeLocation> GeocodeAsync(string address, string city = null)
        {
            var requestParameters = new Dictionary<string, string>
            {
                { "address", address },
                { "callback", Options.Callback },
                { "key", Options.AccessKey },
                { "output", Options.Output }
            };
            if (!city.IsNullOrWhiteSpace())
            {
                requestParameters.Add("region", city);
            }
            var tencentMapPath = "/ws/geocoder/v1";
            if (!Options.SecretKey.IsNullOrWhiteSpace())
            {
                var sig = TencentSecretKeyCaculater.CalcSecretKey(tencentMapPath, Options.SecretKey, requestParameters);
                requestParameters.Add("sig", sig);
            }
            var tencentLocationResponse = await GetTencentMapResponseAsync<TencentGeocodeResponse>(TencentMapApiUrl, tencentMapPath, requestParameters);
            var location = new GecodeLocation
            {
                Confidence = tencentLocationResponse.Result.Reliability,
                Latitude = tencentLocationResponse.Result.Location.Lat,
                Longitude = tencentLocationResponse.Result.Location.Lng,
                Level = tencentLocationResponse.Result.Level.ToString()
            };
            location.AddAdditional("TencentLocation", tencentLocationResponse.Result);

            return location;
        }

        public virtual async Task<ReGeocodeLocation> ReGeocodeAsync(double lat, double lng, int radius = 1000)
        {
            var requestParameters = new Dictionary<string, string>
            {
                { "callback", Options.Callback },
                { "get_poi", Options.GetPoi },
                { "key", Options.AccessKey },
                { "location", $"{lat},{lng}" },
                { "output", Options.Output },
                { "poi_options", "radius=" + radius.ToString() }
            };
            var tencentMapPath = "/ws/geocoder/v1";
            if (!Options.SecretKey.IsNullOrWhiteSpace())
            {
                var sig = TencentSecretKeyCaculater.CalcSecretKey(tencentMapPath, Options.SecretKey, requestParameters);
                requestParameters.Add("sig", sig);
            }
            var tencentLocationResponse = await GetTencentMapResponseAsync<TencentReGeocodeResponse>(TencentMapApiUrl, tencentMapPath, requestParameters);
            var location = new ReGeocodeLocation
            {
                Street = tencentLocationResponse.Result.AddressComponent.Street,
                AdCode = tencentLocationResponse.Result.AddressInfo?.NationCode,
                Address = tencentLocationResponse.Result.Address,
                FormattedAddress = tencentLocationResponse.Result.FormattedAddress?.ReCommend,
                City = tencentLocationResponse.Result.AddressComponent.City,
                Country = tencentLocationResponse.Result.AddressComponent.Nation,
                District = tencentLocationResponse.Result.AddressComponent.District,
                Number = tencentLocationResponse.Result.AddressComponent.StreetNumber,
                Province = tencentLocationResponse.Result.AddressComponent.Province,
                Town = tencentLocationResponse.Result.AddressReference.Town.Title,
                Pois = tencentLocationResponse.Result.Pois.Select(p =>
                {
                    var poi = new Poi
                    {
                        Address = p.Address,
                        Name = p.Title,
                        Tag = p.Id,
                        Type = p.CateGory,
                        Distance = Convert.ToInt32(p.Distance)
                    };

                    return poi;
                }).ToList()
            };
            if ((location.Address.IsNullOrWhiteSpace() ||
                location.FormattedAddress.IsNullOrWhiteSpace()) &&
                location.Pois.Any())
            {
                var nearPoi = location.Pois.OrderBy(x => x.Distance).FirstOrDefault();
                if (nearPoi != null)
                {
                    location.Address = nearPoi.Address;
                    location.FormattedAddress = nearPoi.Name;
                }
            }
            location.AddAdditional("TencentLocation", tencentLocationResponse.Result);

            return location;
        }

        protected virtual async Task<string> MakeRequestAndGetResultAsync(string url)
        {
            var client = HttpClientFactory.CreateClient(TencentLocationHttpConsts.HttpClientName);
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);

            var response = await client.SendAsync(requestMessage, GetCancellationToken());
            if (!response.IsSuccessStatusCode)
            {
                throw new AbpException($"Tencent http request service returns error! HttpStatusCode: {response.StatusCode}, ReasonPhrase: {response.ReasonPhrase}");
            }
            var resultContent = await response.Content.ReadAsStringAsync();

            return resultContent;
        }

        protected virtual CancellationToken GetCancellationToken()
        {
            return CancellationTokenProvider.Token;
        }

        protected virtual async Task<TResponse> GetTencentMapResponseAsync<TResponse>(string url, string path, IDictionary<string, string> paramters)
            where TResponse : TencentLocationResponse
        {
            var requestUrl = BuildRequestUrl(url, path, paramters);
            var responseContent = await MakeRequestAndGetResultAsync(requestUrl);
            var tencentLocationResponse = JsonSerializer.Deserialize<TResponse>(responseContent);
            if (!tencentLocationResponse.IsSuccessed)
            {
                if (Options.VisibleErrorToClient)
                {
                    var localizerFactory = ServiceProvider.GetRequiredService<IStringLocalizerFactory>();
                    var localizerErrorMessage = tencentLocationResponse.GetErrorMessage(Options.VisibleErrorToClient).Localize(localizerFactory);
                    var localizer = ServiceProvider.GetRequiredService<IStringLocalizer<TencentLocationResource>>();
                    localizerErrorMessage = localizer["ResolveLocationFailed", localizerErrorMessage];
                    throw new UserFriendlyException(localizerErrorMessage);
                }
                throw new AbpException($"Resolution address failed:{tencentLocationResponse.Message}!");
            }
            return tencentLocationResponse;
        }

        protected virtual string BuildRequestUrl(string uri, string path, IDictionary<string, string> parameters)
        {
            var requestUrlBuilder = new StringBuilder(128);
            requestUrlBuilder.Append(uri);
            requestUrlBuilder.Append(path).Append("?");
            foreach (var parameter in parameters)
            {
                requestUrlBuilder.AppendFormat("{0}={1}", parameter.Key, parameter.Value);
                requestUrlBuilder.Append("&");
            }
            requestUrlBuilder.Remove(requestUrlBuilder.Length - 1, 1);
            return requestUrlBuilder.ToString();
        }
    }
}
