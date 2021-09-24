// <copyright file="OtlpHttpTraceExportClientTests.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation;
using OpenTelemetry.Exporter.OpenTelemetryProtocol.Implementation.ExportClient;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;
using OtlpCollector = Opentelemetry.Proto.Collector.Trace.V1;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Tests.Implementation.ExportClient
{
    public class OtlpHttpTraceExportClientTests
    {
        static OtlpHttpTraceExportClientTests()
        {
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            Activity.ForceDefaultIdFormat = true;

            var listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            };

            ActivitySource.AddActivityListener(listener);
        }

        [Fact]
        public void NewOtlpHttpTraceExportClient_DefaultOtlpExporterOptions_ExportClientHasDefaulProperties()
        {
            var client = new OtlpHttpTraceExportClient(new OtlpExporterOptions());

            Assert.NotNull(client.HttpHandler);
            Assert.IsType<HttpHandler>(client.HttpHandler);
        }

        [Fact]
        public void NewOtlpHttpTraceExportClient_OtlpExporterOptions_ExporterHasCorrectProperties()
        {
            var header1 = new { Name = "hdr1", Value = "val1" };
            var header2 = new { Name = "hdr2", Value = "val2" };

            var options = new OtlpExporterOptions
            {
                Headers = $"{header1.Name}={header1.Value}, {header2.Name} = {header2.Value}",
            };

            var client = new OtlpHttpTraceExportClient(options, new NoopHttpHandler());

            Assert.NotNull(client.HttpHandler);
            Assert.IsType<NoopHttpHandler>(client.HttpHandler);

            Assert.Equal(2, client.Headers.Count);
            Assert.Contains(client.Headers, kvp => kvp.Key == header1.Name && kvp.Value == header1.Value);
            Assert.Contains(client.Headers, kvp => kvp.Key == header2.Name && kvp.Value == header2.Value);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SendExportRequest_ExportTraceServiceRequest_SendsCorrectHttpRequest(bool includeServiceNameInResource)
        {
            // Arrange
            var activitySourceName = nameof(this.SendExportRequest_ExportTraceServiceRequest_SendsCorrectHttpRequest);
            using var activitySource = new ActivitySource(activitySourceName);

            var activityName = $"activity-{nameof(this.SendExportRequest_ExportTraceServiceRequest_SendsCorrectHttpRequest)}";
            using var activity = activitySource.StartActivity(activityName, ActivityKind.Producer);

            var header1 = new { Name = "hdr1", Value = "val1" };
            var header2 = new { Name = "hdr2", Value = "val2" };

            var options = new OtlpExporterOptions
            {
                Endpoint = new Uri("http://localhost:4317"),
                Headers = $"{header1.Name}={header1.Value}, {header2.Name} = {header2.Value}",
            };

            var httpHandlerMock = new Mock<IHttpHandler>();

            HttpRequestMessage httpRequest = null;
            var httpRequestContent = Array.Empty<byte>();

            httpHandlerMock.Setup(h => h.Send(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
                .Callback<HttpRequestMessage, CancellationToken>(async (r, ct) =>
                {
                    httpRequest = r;

                    // We have to capture content as it can't be accessed after request is disposed inside of SendExportRequest method
                    httpRequestContent = await r.Content.ReadAsByteArrayAsync();
                });

            var exportClient = new OtlpHttpTraceExportClient(options, httpHandlerMock.Object);

            var exporter = new OtlpTraceExporter(options, exportClient);

            var resourceBuilder = ResourceBuilder.CreateEmpty();
            if (includeServiceNameInResource)
            {
                resourceBuilder.AddAttributes(
                    new List<KeyValuePair<string, object>>
                    {
                        new KeyValuePair<string, object>(ResourceSemanticConventions.AttributeServiceName, "service_name"),
                        new KeyValuePair<string, object>(ResourceSemanticConventions.AttributeServiceNamespace, "ns_1"),
                    });
            }

            var builder = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resourceBuilder);

            using var openTelemetrySdk = builder.Build();

            exporter.ParentProvider = openTelemetrySdk;

            var request = new OtlpCollector.ExportTraceServiceRequest();
            request.AddBatch(exporter.ProcessResource, new Batch<Activity>(activity));

            // Act
            var result = exportClient.SendExportRequest(request);

            // Assert
            httpHandlerMock.Verify(m => m.Send(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()), Times.Once());

            Assert.True(result);
            Assert.NotNull(httpRequest);
            Assert.Equal(HttpMethod.Post, httpRequest.Method);
            Assert.Equal("http://localhost:4317/v1/traces", httpRequest.RequestUri.AbsoluteUri);
            Assert.Equal(2, httpRequest.Headers.Count());
            Assert.Contains(httpRequest.Headers, h => h.Key == header1.Name && h.Value.First() == header1.Value);
            Assert.Contains(httpRequest.Headers, h => h.Key == header2.Name && h.Value.First() == header2.Value);

            Assert.NotNull(httpRequest.Content);
            Assert.IsType<ByteArrayContent>(httpRequest.Content);
            Assert.Contains(httpRequest.Content.Headers, h => h.Key == "Content-Type" && h.Value.First() == OtlpHttpTraceExportClient.MediaContentType);

            var exportTraceRequest = OtlpCollector.ExportTraceServiceRequest.Parser.ParseFrom(httpRequestContent);
            Assert.NotNull(exportTraceRequest);

            Assert.Single(exportTraceRequest.ResourceSpans);

            var resourceSpan = exportTraceRequest.ResourceSpans.First();
            if (includeServiceNameInResource)
            {
                Assert.Contains(resourceSpan.Resource.Attributes, (kvp) => kvp.Key == ResourceSemanticConventions.AttributeServiceName && kvp.Value.StringValue == "service_name");
                Assert.Contains(resourceSpan.Resource.Attributes, (kvp) => kvp.Key == ResourceSemanticConventions.AttributeServiceNamespace && kvp.Value.StringValue == "ns_1");
            }
            else
            {
                Assert.Contains(resourceSpan.Resource.Attributes, (kvp) => kvp.Key == ResourceSemanticConventions.AttributeServiceName && kvp.Value.ToString().Contains("unknown_service:"));
            }

            Assert.Single(resourceSpan.InstrumentationLibrarySpans);
        }

        [Fact]
        public void CancelExportRequest_PendingHttpRequestsCancelled()
        {
            var httpHandlerMock = new Mock<IHttpHandler>();

            var client = new OtlpHttpTraceExportClient(new OtlpExporterOptions(), httpHandlerMock.Object);

            var result = client.CancelExportRequest(10);

            httpHandlerMock.Verify(m => m.CancelPendingRequests(), Times.Once());
        }

        private class NoopHttpHandler : IHttpHandler
        {
            public void CancelPendingRequests()
            {
            }

            public HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken = default)
            {
                return null;
            }
        }
    }
}
