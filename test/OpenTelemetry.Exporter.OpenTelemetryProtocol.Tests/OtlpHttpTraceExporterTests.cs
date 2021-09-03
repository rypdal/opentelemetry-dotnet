// <copyright file="OtlpHttpTraceExporterTests.cs" company="OpenTelemetry Authors">
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
using OpenTelemetry.Trace;
using Xunit;
using OtlpCollector = Opentelemetry.Proto.Collector.Trace.V1;

namespace OpenTelemetry.Exporter.OpenTelemetryProtocol.Tests
{
    public class OtlpHttpTraceExporterTests
    {
        static OtlpHttpTraceExporterTests()
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
        public void OtlpExporter_BadArgs()
        {
            TracerProviderBuilder builder = null;
            Assert.Throws<ArgumentNullException>(() => builder.AddOtlpHttpExporter());
        }

        [Fact]
        public void NewOtlpHttpTraceExporter_ExportHasDefaulProperties()
        {
            var exporter = new OtlpHttpTraceExporter(new OtlpExporterOptions());

            Assert.NotNull(exporter.HttpHandler);
            Assert.IsType<HttpHandler>(exporter.HttpHandler);
        }

        [Fact]
        public void NewOtlpHttpTraceExporter_ExporterHasDefinedProperties()
        {
            var header1 = new { Name = "hdr1", Value = "val1" };
            var header2 = new { Name = "hdr2", Value = "val2" };

            var options = new OtlpExporterOptions
            {
                Headers = $"{header1.Name}={header1.Value}, {header2.Name} = {header2.Value}",
            };

            var exporter = new OtlpHttpTraceExporter(options, new NoopHttpHandler());

            Assert.NotNull(exporter.HttpHandler);
            Assert.IsType<NoopHttpHandler>(exporter.HttpHandler);

            Assert.Equal(2, exporter.Headers.Count);
            Assert.Contains(exporter.Headers, kvp => kvp.Key == header1.Name && kvp.Value == header1.Value);
            Assert.Contains(exporter.Headers, kvp => kvp.Key == header2.Name && kvp.Value == header2.Value);
        }

        [Fact]
        public async Task Export_ActivityBatch_SendsCorrectHttpRequest()
        {
            using var activitySource = new ActivitySource(nameof(this.Export_ActivityBatch_SendsCorrectHttpRequest));

            using var activity = activitySource.StartActivity($"activity-{nameof(this.Export_ActivityBatch_SendsCorrectHttpRequest)}", ActivityKind.Producer);

            var header1 = new { Name = "hdr1", Value = "val1" };
            var header2 = new { Name = "hdr2", Value = "val2" };

            var options = new OtlpExporterOptions
            {
                Endpoint = new Uri("http://localhost:4318"),
                Headers = $"{header1.Name}={header1.Value}, {header2.Name} = {header2.Value}",
            };

            var httpHandlerMock = new Mock<IHttpHandler>();
            HttpRequestMessage httpRequest = null;
            httpHandlerMock.Setup(h => h.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()))
                .Callback<HttpRequestMessage, CancellationToken>((r, ct) => httpRequest = r);

            var exporter = new OtlpHttpTraceExporter(options, httpHandlerMock.Object);

            var result = exporter.Export(new Batch<Activity>(activity));

            httpHandlerMock.Verify(m => m.SendAsync(It.IsAny<HttpRequestMessage>(), It.IsAny<CancellationToken>()), Times.Once());

            Assert.Equal(ExportResult.Success, result);
            Assert.NotNull(httpRequest);
            Assert.Equal(HttpMethod.Post, httpRequest.Method);
            Assert.Equal(options.Endpoint.AbsoluteUri, httpRequest.RequestUri.AbsoluteUri);
            Assert.Equal(2, httpRequest.Headers.Count());
            Assert.Contains(httpRequest.Headers, h => h.Key == header1.Name && h.Value.First() == header1.Value);
            Assert.Contains(httpRequest.Headers, h => h.Key == header2.Name && h.Value.First() == header2.Value);

            Assert.NotNull(httpRequest.Content);
            Assert.IsType<ByteArrayContent>(httpRequest.Content);
            Assert.Contains(httpRequest.Content.Headers, h => h.Key == "Content-Type" && h.Value.First() == OtlpHttpTraceExporter.MediaContentType);

            var exportTraceRequest = OtlpCollector.ExportTraceServiceRequest.Parser.ParseFrom(await httpRequest.Content.ReadAsByteArrayAsync());
            Assert.NotNull(exportTraceRequest);

            Assert.Single(exportTraceRequest.ResourceSpans);
            Assert.Single(exportTraceRequest.ResourceSpans.First().Resource.Attributes);
        }

        [Fact]
        public void Shutdown_PendingHttpRequestsCancelled()
        {
            var httpHandlerMock = new Mock<IHttpHandler>();

            var exporter = new OtlpHttpTraceExporter(new OtlpExporterOptions(), httpHandlerMock.Object);

            var result = exporter.Shutdown();

            httpHandlerMock.Verify(m => m.CancelPendingRequests(), Times.Once());
        }

        private class NoopHttpHandler : IHttpHandler
        {
            public void CancelPendingRequests()
            {
            }

            public void Dispose()
            {
            }

            public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
            {
                return null;
            }
        }
    }
}
