﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Http;
using System.Web.Http.Description;
using System.Xml.Serialization;
using VirtoCommerce.Domain.Catalog.Model;
using VirtoCommerce.Domain.Catalog.Services;
using VirtoCommerce.Domain.Commerce.Model;
using VirtoCommerce.Domain.Inventory.Model;
using VirtoCommerce.Domain.Inventory.Services;
using VirtoCommerce.Domain.Search.Filters;
using VirtoCommerce.Domain.Search.Model;
using VirtoCommerce.Domain.Search.Services;
using VirtoCommerce.Domain.Store.Model;
using VirtoCommerce.Domain.Store.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DynamicProperties;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SearchModule.Web.BackgroundJobs;
using VirtoCommerce.SearchModule.Web.Services;
using webModel = VirtoCommerce.SearchModule.Web.Model;

namespace VirtoCommerce.SearchModule.Web.Controllers.Api
{
    [RoutePrefix("api/search")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class SearchModuleController : ApiController
    {
        private const string _filteredBrowsingPropertyName = "FilteredBrowsing";

        private readonly ISearchProvider _searchProvider;
        private readonly ISearchConnection _searchConnection;
        private readonly SearchIndexJobsScheduler _scheduler;
        private readonly IStoreService _storeService;
        private readonly ISecurityService _securityService;
        private readonly IPermissionScopeService _permissionScopeService;
        private readonly IPropertyService _propertyService;
        private readonly IBrowseFilterService _browseFilterService;
        private readonly IItemBrowsingService _browseService;
        private readonly IInventoryService _inventoryService;

        public SearchModuleController(ISearchProvider searchProvider, ISearchConnection searchConnection, SearchIndexJobsScheduler scheduler, IStoreService storeService, ISecurityService securityService, IPermissionScopeService permissionScopeService, IPropertyService propertyService, IBrowseFilterService browseFilterService, IItemBrowsingService browseService, IInventoryService inventoryService)
        {
            _searchProvider = searchProvider;
            _searchConnection = searchConnection;
            _scheduler = scheduler;
            _storeService = storeService;
            _securityService = securityService;
            _permissionScopeService = permissionScopeService;
            _propertyService = propertyService;
            _browseFilterService = browseFilterService;
            _browseService = browseService;
            _inventoryService = inventoryService;
        }

        [HttpGet]
        [Route("catalogitem")]
        [ResponseType(typeof(ISearchResults))]
        [CheckPermission(Permission = "VirtoCommerce.Search:Debug")]
        public IHttpActionResult Search([FromUri]CatalogIndexedSearchCriteria criteria)
        {
            criteria = criteria ?? new CatalogIndexedSearchCriteria();
            var scope = _searchConnection.Scope;
            var searchResults = _searchProvider.Search(scope, criteria);
            return Ok(searchResults);
        }

        [HttpGet]
        [Route("catalogitem/rebuild")]
        [CheckPermission(Permission = "VirtoCommerce.Search:Index:Rebuild")]
        public IHttpActionResult Rebuild()
        {
            var jobId = _scheduler.ScheduleRebuildIndex();
            var result = new { Id = jobId };
            return Ok(result);
        }

        /// <summary>
        /// Get filter properties for store
        /// </summary>
        /// <remarks>
        /// Returns all store catalog properties: selected properties are ordered manually, unselected properties are ordered by name.
        /// </remarks>
        /// <param name="id">Store ID</param>
        /// <responce code="404">Store not found</responce>
        /// <responce code="200"></responce>
        [HttpGet]
        [Route("storefilterproperties/{id}")]
        [ResponseType(typeof(webModel.FilterProperty[]))]
        public IHttpActionResult GetFilterProperties(string id)
        {
            var store = _storeService.GetById(id);
            if (store == null)
            {
                return NotFound();
            }

            CheckCurrentUserHasPermissionForObjects("read", store);

            var allProperties = GetAllCatalogProperties(store.Catalog);
            var selectedPropertyNames = GetSelectedFilterProperties(store);

            var filterProperties = allProperties
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => ConvertToFilterProperty(g.FirstOrDefault(), selectedPropertyNames))
                .OrderBy(p => p.Name)
                .ToArray();

            // Keep the selected properties order
            var result = selectedPropertyNames
                .SelectMany(n => filterProperties.Where(p => string.Equals(p.Name, n)))
                .Union(filterProperties.Where(p => !selectedPropertyNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                .ToArray();

            return Ok(result);
        }

        /// <summary>
        /// Set filter properties for store
        /// </summary>
        /// <param name="id">Store ID</param>
        /// <param name="filterProperties"></param>
        /// <responce code="404">Store not found</responce>
        /// <responce code="204"></responce>
        [HttpPut]
        [Route("storefilterproperties/{id}")]
        [ResponseType(typeof(void))]
        public IHttpActionResult SetFilterProperties(string id, webModel.FilterProperty[] filterProperties)
        {
            var store = _storeService.GetById(id);
            if (store == null)
            {
                return NotFound();
            }

            CheckCurrentUserHasPermissionForObjects("read", store);

            var allProperties = GetAllCatalogProperties(store.Catalog);

            var selectedPropertyNames = filterProperties
                .Where(p => p.IsSelected)
                .Select(p => p.Name)
                .Distinct()
                .ToArray();

            // Keep the selected properties order
            var selectedDictionaryProperties = selectedPropertyNames
                .SelectMany(n => allProperties.Where(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var attributes = selectedDictionaryProperties
                .Select(ConvertToAttributeFilter)
                .GroupBy(a => a.Key)
                .Select(g => new AttributeFilter { Key = g.Key, Values = GetDistinctValues(g.SelectMany(p => p.Values)) })
                .ToArray();

            SetFilteredBrowsingAttributes(store, attributes);
            _storeService.Update(new[] { store });

            return StatusCode(HttpStatusCode.NoContent);
        }

        /// <summary>
        /// Search for store products
        /// </summary>
        /// <param name="request">Search parameters</param>
        [HttpGet]
        [Route("")]
        [ResponseType(typeof(SearchResult))]
        [ClientCache(Duration = 30)]
        public IHttpActionResult Search([FromUri] SearchCriteria request)
        {
            request = request ?? new SearchCriteria();
            Normalize(request);

            var context = new Dictionary<string, object>
            {
                { "StoreId", request.StoreId },
            };

            var store = _storeService.GetById(request.StoreId);
            if (store == null)
            {
                throw new NullReferenceException(request.StoreId + " not found");
            }

            var catalog = store.Catalog;

            string categoryId = null;

            var criteria = new CatalogIndexedSearchCriteria
            {
                Locale = request.LanguageCode,
                Catalog = catalog.ToLowerInvariant(),
                IsFuzzySearch = true,
            };

            if (!string.IsNullOrWhiteSpace(request.Outline))
            {
                criteria.Outlines.Add(string.Format(CultureInfo.InvariantCulture, "{0}/{1}*", catalog, request.Outline));
                categoryId = request.Outline.Split('/').Last();
                context.Add("CategoryId", categoryId);
            }

            #region Filters
            // Now fill in filters
            var filters = _browseFilterService.GetFilters(context);

            // Add all filters
            foreach (var filter in filters)
            {
                criteria.Add(filter);
            }

            // apply terms
            var terms = ParseKeyValues(request.Terms);
            if (terms.Any())
            {
                var filtersWithValues = filters
                    .Where(x => (!(x is PriceRangeFilter) || ((PriceRangeFilter)x).Currency.Equals(request.Currency, StringComparison.OrdinalIgnoreCase)))
                    .Select(x => new { Filter = x, Values = x.GetValues() })
                    .ToList();

                foreach (var term in terms)
                {
                    var filter = filters.SingleOrDefault(x => x.Key.Equals(term.Key, StringComparison.OrdinalIgnoreCase)
                        && (!(x is PriceRangeFilter) || ((PriceRangeFilter)x).Currency.Equals(request.Currency, StringComparison.OrdinalIgnoreCase)));

                    // handle special filter term with a key = "tags", it contains just values and we need to determine which filter to use
                    if (filter == null && term.Key == "tags")
                    {
                        foreach (var termValue in term.Values)
                        {
                            // try to find filter by value
                            var foundFilter = filtersWithValues.FirstOrDefault(x => x.Values.Any(y => y.Id.Equals(termValue)));

                            if (foundFilter != null)
                            {
                                filter = foundFilter.Filter;

                                var appliedFilter = _browseFilterService.Convert(filter, term.Values);
                                criteria.Apply(appliedFilter);
                            }
                        }
                    }
                    else
                    {
                        var appliedFilter = _browseFilterService.Convert(filter, term.Values);
                        criteria.Apply(appliedFilter);
                    }
                }
            }
            #endregion

            #region Facets
            // apply facet filters
            var facets = ParseKeyValues(request.Facets);
            foreach (var facet in facets)
            {
                var filter = filters.SingleOrDefault(
                    x => x.Key.Equals(facet.Key, StringComparison.OrdinalIgnoreCase)
                        && (!(x is PriceRangeFilter)
                            || ((PriceRangeFilter)x).Currency.Equals(request.Currency, StringComparison.OrdinalIgnoreCase)));

                var appliedFilter = _browseFilterService.Convert(filter, facet.Values);
                criteria.Apply(appliedFilter);
            }
            #endregion

            //criteria.ClassTypes.Add("Product");
            criteria.RecordsToRetrieve = request.Take <= 0 ? 10 : request.Take;
            criteria.StartingRecord = request.Skip;
            criteria.Pricelists = request.PricelistIds;
            criteria.Currency = request.Currency;
            criteria.StartDateFrom = request.StartDateFrom;
            criteria.SearchPhrase = request.Keyword;

            #region sorting

            if (!string.IsNullOrEmpty(request.Sort))
            {
                var isDescending = "desc".Equals(request.SortOrder, StringComparison.OrdinalIgnoreCase);

                SearchSort sortObject = null;

                switch (request.Sort.ToLowerInvariant())
                {
                    case "price":
                        if (criteria.Pricelists != null)
                        {
                            sortObject = new SearchSort(
                                criteria.Pricelists.Select(
                                    priceList =>
                                        new SearchSortField(String.Format("price_{0}_{1}", criteria.Currency.ToLower(), priceList.ToLower()))
                                        {
                                            IgnoredUnmapped = true,
                                            IsDescending = isDescending,
                                            DataType = SearchSortField.DOUBLE
                                        })
                                    .ToArray());
                        }
                        break;
                    case "position":
                        sortObject =
                            new SearchSort(
                                new SearchSortField(string.Concat("sort", catalog, categoryId).ToLower())
                                {
                                    IgnoredUnmapped = true,
                                    IsDescending = isDescending
                                });
                        break;
                    case "name":
                        sortObject = new SearchSort("name", isDescending);
                        break;
                    case "rating":
                        sortObject = new SearchSort(criteria.ReviewsAverageField, isDescending);
                        break;
                    case "reviews":
                        sortObject = new SearchSort(criteria.ReviewsTotalField, isDescending);
                        break;
                    default:
                        sortObject = CatalogIndexedSearchCriteria.DefaultSortOrder;
                        break;
                }

                criteria.Sort = sortObject;
            }

            #endregion

            //Load ALL products 
            var searchResults = _browseService.SearchItems(criteria, ItemResponseGroup.ItemInfo);

            // populate inventory
            //if ((request.ResponseGroup & ItemResponseGroup.ItemProperties) == ItemResponseGroup.ItemProperties)
            if ((request.ResponseGroup & SearchResponseGroup.WithProperties) == SearchResponseGroup.WithProperties)
            {
                PopulateInventory(store.FulfillmentCenter, searchResults.Products);
            }

            return Ok(searchResults);
        }

        private static void Normalize(SearchCriteria request)
        {
            request.Keyword = request.Keyword.EmptyToNull();
            request.Sort = request.Sort.EmptyToNull();
            request.SortOrder = request.SortOrder.EmptyToNull();

            if (!string.IsNullOrEmpty(request.Keyword))
            {
                request.Keyword = request.Keyword.EscapeSearchTerm();
            }
        }

        protected void CheckCurrentUserHasPermissionForObjects(string permission, params object[] objects)
        {
            //Scope bound security check
            var scopes = objects.SelectMany(x => _permissionScopeService.GetObjectPermissionScopeStrings(x)).Distinct().ToArray();
            if (!_securityService.UserHasAnyPermission(User.Identity.Name, scopes, permission))
            {
                throw new HttpResponseException(HttpStatusCode.Unauthorized);
            }
        }


        private static string[] GetSelectedFilterProperties(Store store)
        {
            var result = new List<string>();

            var browsing = GetFilteredBrowsing(store);
            if (browsing?.Attributes != null)
            {
                result.AddRange(browsing.Attributes.Select(a => a.Key));
            }

            return result.ToArray();
        }

        private static FilteredBrowsing GetFilteredBrowsing(Store store)
        {
            FilteredBrowsing result = null;

            var filterSettingValue = store.GetDynamicPropertyValue(_filteredBrowsingPropertyName, string.Empty);

            if (!string.IsNullOrEmpty(filterSettingValue))
            {
                var reader = new StringReader(filterSettingValue);
                var serializer = new XmlSerializer(typeof(FilteredBrowsing));
                result = serializer.Deserialize(reader) as FilteredBrowsing;
            }

            return result;
        }

        private static void SetFilteredBrowsingAttributes(Store store, AttributeFilter[] attributes)
        {
            var browsing = GetFilteredBrowsing(store) ?? new FilteredBrowsing();
            browsing.Attributes = attributes;
            var serializer = new XmlSerializer(typeof(FilteredBrowsing));
            var builder = new StringBuilder();
            var writer = new StringWriter(builder);
            serializer.Serialize(writer, browsing);
            var value = builder.ToString();

            var property = store.DynamicProperties.FirstOrDefault(p => p.Name == _filteredBrowsingPropertyName);

            if (property == null)
            {
                property = new DynamicObjectProperty { Name = _filteredBrowsingPropertyName };
                store.DynamicProperties.Add(property);
            }

            property.Values = new List<DynamicPropertyObjectValue>(new[] { new DynamicPropertyObjectValue { Value = value } });
        }

        private Property[] GetAllCatalogProperties(string catalogId)
        {
            var properties = _propertyService.GetAllCatalogProperties(catalogId);

            var result = properties
                .GroupBy(p => p.Id)
                .Select(g => g.FirstOrDefault())
                .OrderBy(p => p.Name)
                .ToArray();

            return result;
        }

        private static AttributeFilterValue[] GetDistinctValues(IEnumerable<AttributeFilterValue> values)
        {
            return values
                .Where(v => !string.IsNullOrEmpty(v.Id) && !string.IsNullOrEmpty(v.Value))
                .GroupBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.FirstOrDefault())
                .OrderBy(v => v.Value)
                .ToArray();
        }

        private static List<StringKeyValues> ParseKeyValues(string[] items)
        {
            var result = new List<StringKeyValues>();

            if (items != null)
            {
                var nameValueDelimeter = new[] { ':' };
                var valuesDelimeter = new[] { ',' };

                result.AddRange(items
                    .Select(item => item.Split(nameValueDelimeter, 2))
                    .Where(item => item.Length == 2)
                    .Select(item => new StringKeyValues { Key = item[0], Values = item[1].Split(valuesDelimeter, StringSplitOptions.RemoveEmptyEntries) })
                    .GroupBy(item => item.Key)
                    .Select(g => new StringKeyValues { Key = g.Key, Values = g.SelectMany(i => i.Values).Distinct().ToArray() })
                    );
            }

            return result;
        }

        private void PopulateInventory(FulfillmentCenter center, IEnumerable<CatalogProduct> products)
        {
            if (center == null || products == null || !products.Any())
                return;

            var inventories = _inventoryService.GetProductsInventoryInfos(products.Select(x => x.Id).ToArray()).ToList();

            foreach (var product in products)
            {
                var productInventory = inventories.FirstOrDefault(x => x.ProductId == product.Id && x.FulfillmentCenterId == center.Id);
                if (productInventory != null)
                    product.Inventories = new List<InventoryInfo> { productInventory };
            }
        }

        private static webModel.FilterProperty ConvertToFilterProperty(Property property, string[] selectedPropertyNames)
        {
            return new webModel.FilterProperty
            {
                Name = property.Name,
                IsSelected = selectedPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
            };
        }

        private AttributeFilter ConvertToAttributeFilter(Property property)
        {
            var values = _propertyService.SearchDictionaryValues(property.Id, null);

            var result = new AttributeFilter
            {
                Key = property.Name,
                Values = values.Select(ConvertToAttributeFilterValue).ToArray(),
            };

            return result;
        }

        private static AttributeFilterValue ConvertToAttributeFilterValue(PropertyDictionaryValue dictionaryValue)
        {
            var result = new AttributeFilterValue
            {
                Id = dictionaryValue.Alias,
                Value = dictionaryValue.Value,
            };

            return result;
        }

        private class StringKeyValues
        {
            public string Key { get; set; }
            public string[] Values { get; set; }
        }
    }
}
