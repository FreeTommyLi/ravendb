// -----------------------------------------------------------------------
//  <copyright file="RDBQA_11.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Threading.Tasks;
using FastTests;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Expiration;
using Raven.Client.Documents.Smuggler;
using Raven.Client.ServerWide.Operations;
using Raven.Client.Util;
using Raven.Server.Documents.Expiration;
using Raven.Server.Utils;
using Sparrow.Extensions;
using Xunit;

namespace SlowTests.Issues
{
    public class RDBQA_11 : RavenTestBase
    {
        private class Product
        {
            public string Id { get; set; }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task SmugglerWithoutExcludeExpiredDocumentsShouldWork()
        {
            var path = Path.GetTempFileName();

            try
            {
                using (var store = GetDocumentStore())
                {
                    Initialize(store);

                    await store.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions(), path);
                }

                using (var store = GetDocumentStore())
                {
                    await store.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions(), path);

                    using (var session = store.OpenSession())
                    {
                        var product1 = session.Load<Product>("products/1-A");
                        var product2 = session.Load<Product>("products/2-A");
                        var product3 = session.Load<Product>("products/3-A");

                        Assert.NotNull(product1);
                        Assert.NotNull(product2);
                        Assert.NotNull(product3);
                    }
                }
            }
            finally
            {
                IOExtensions.DeleteDirectory(path);
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task SmugglerWithExcludeExpiredDocumentsShouldWork1()
        {
            var path = Path.GetTempFileName();

            try
            {
                using (var store = GetDocumentStore())
                {
                    Initialize(store);

                    await store.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions { IncludeExpired = false }, path);
                }

                using (var store = GetDocumentStore())
                {
                    await store.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions { IncludeExpired = false }, path);

                    using (var session = store.OpenSession())
                    {
                        var product1 = session.Load<Product>("products/1-A");
                        var product2 = session.Load<Product>("products/2-A");
                        var product3 = session.Load<Product>("products/3-A");

                        Assert.NotNull(product1);
                        Assert.Null(product2);
                        Assert.NotNull(product3);
                    }
                }
            }
            finally
            {
                IOExtensions.DeleteDirectory(path);
            }
        }

        [Fact, Trait("Category", "Smuggler")]
        public async Task SmugglerWithExcludeExpiredDocumentsShouldWork2()
        {
            var path = Path.GetTempFileName();

            try
            {
                using (var store = GetDocumentStore())
                {
                    Initialize(store);

                    await store.Smuggler.ExportAsync(new DatabaseSmugglerExportOptions { IncludeExpired = false }, path);
                }

                using (var store = GetDocumentStore())
                {
                    var database = GetDocumentDatabaseInstanceFor(store).Result;
                    database.Time.UtcDateTime = () => DateTime.UtcNow.AddMinutes(10);

                    await store.Smuggler.ImportAsync(new DatabaseSmugglerImportOptions { IncludeExpired = false }, path);

                    using (var session = store.OpenSession())
                    {
                        var product1 = session.Load<Product>("products/1-A");
                        var product2 = session.Load<Product>("products/2-A");
                        var product3 = session.Load<Product>("products/3-A");

                        Assert.NotNull(product1);
                        Assert.Null(product2);
                        Assert.Null(product3);
                    }
                }
            }
            finally
            {
                IOExtensions.DeleteDirectory(path);
            }
        }

        private static void Initialize(IDocumentStore store)
        {
            SetupExpiration(store);

            var product1 = new Product();
            var product2 = new Product();
            var product3 = new Product();

            var future = SystemTime.UtcNow.AddMinutes(5);
            var past = SystemTime.UtcNow.AddMinutes(-5);
            using (var session = store.OpenSession())
            {
                session.Store(product1);
                session.Store(product2);
                session.Store(product3);

                session.Advanced.GetMetadataFor(product2)[Constants.Documents.Metadata.Expires] = past.GetDefaultRavenFormat(isUtc: true);
                session.Advanced.GetMetadataFor(product3)[Constants.Documents.Metadata.Expires] = future.GetDefaultRavenFormat(isUtc: true);

                session.SaveChanges();
            }
        }

        private static void SetupExpiration(IDocumentStore store)
        {
            using (var session = store.OpenSession())
            {
                var config = new ExpirationConfiguration
                {
                    Disabled = false,
                    DeleteFrequencyInSec = 100,
                };
                store.Maintenance.Send(new ConfigureExpirationOperation(config));
                session.SaveChanges();
            }
        }
    }
}
