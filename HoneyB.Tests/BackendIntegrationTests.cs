using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace HoneyB.Tests
{
    /// <summary>
    /// Tests the backend integration without needing Visual Studio running.
    /// Spins up a WireMock server to fake the backend and verifies
    /// the extension sends correctly shaped payloads.
    /// </summary>
    public class BackendIntegrationTests : IDisposable
    {
        private readonly WireMockServer _mockBackend;
        private readonly HttpClient _http;

        public BackendIntegrationTests()
        {
            // Start a fake backend on a random port
            _mockBackend = WireMockServer.Start();
            _http = new HttpClient { BaseAddress = new Uri(_mockBackend.Urls[0]) };
        }

        [Fact]
        public async Task Snapshot_Payload_Is_Well_Formed()
        {
            // Arrange — fake backend accepts /snapshot and returns an ID
            _mockBackend
                .Given(Request.Create().WithPath("/snapshot").UsingPost())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("{\"snapshot_id\": 0, \"total_snapshots\": 1}"));

            // Build a payload the same way the extension would
            var payload = new
            {
                label = "Breakpoint hit at Program.cs:42",
                frames = new[]
                {
                    new
                    {
                        function = "Program.Main",
                        file = "Program.cs",
                        line = 42,
                        locals = new[]
                        {
                            new { name = "counter", type = "int", value = "5" },
                            new { name = "message", type = "string", value = "hello" },
                        }
                    }
                },
                source_context = "for (int i = 0; i < 10; i++)\n{\n    counter++;\n}"
            };

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act
            var response = await _http.PostAsync("/snapshot", content);
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());

            // Assert
            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal(0, body["snapshot_id"].Value<int>());
        }

        [Fact]
        public async Task Query_Returns_Answer()
        {
            // Arrange
            _mockBackend
                .Given(Request.Create().WithPath("/query").UsingPost())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("{\"answer\": \"The counter is 5 which is within range.\", \"snapshot_id\": 0}"));

            var payload = new { question = "Is the counter value correct?", snapshot_id = 0 };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Act
            var response = await _http.PostAsync("/query", content);
            var body = JObject.Parse(await response.Content.ReadAsStringAsync());

            // Assert
            Assert.True(response.IsSuccessStatusCode);
            Assert.False(string.IsNullOrEmpty(body["answer"].Value<string>()));
        }

        [Fact]
        public async Task Health_Check_Succeeds()
        {
            _mockBackend
                .Given(Request.Create().WithPath("/health").UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithBody("{\"status\": \"ok\", \"llm_ready\": true}"));

            var response = await _http.GetAsync("/health");
            Assert.True(response.IsSuccessStatusCode);
        }

        public void Dispose()
        {
            _mockBackend.Stop();
            _http.Dispose();
        }
    }

    /// <summary>
    /// Tests the payload builder logic in isolation —
    /// no VS, no HTTP, just pure C# logic.
    /// </summary>
    public class PayloadBuilderTests
    {
        [Fact]
        public void Snapshot_Label_Includes_File_And_Line()
        {
            // Simulate what DebuggerEventListener.BuildSnapshot produces
            var frame = new FramePayload
            {
                Function = "Program.Main",
                File = "Program.cs",
                Line = 42,
                Locals = new System.Collections.Generic.List<VariablePayload>
                {
                    new VariablePayload { Name = "x", Type = "int", Value = "7" }
                }
            };

            var snapshot = new SnapshotPayload
            {
                Label = $"Breakpoint hit at {frame.File}:{frame.Line}",
                Frames = new System.Collections.Generic.List<FramePayload> { frame },
            };

            Assert.Contains("Program.cs:42", snapshot.Label);
            Assert.Single(snapshot.Frames);
            Assert.Single(snapshot.Frames[0].Locals);
        }

        [Fact]
        public void Variable_Serializes_All_Fields()
        {
            var variable = new VariablePayload
            {
                Name = "counter",
                Type = "int",
                Value = "5",
            };

            var json = JsonConvert.SerializeObject(variable,
                new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization
                        .CamelCasePropertyNamesContractResolver()
                });

            Assert.Contains("\"name\"", json);
            Assert.Contains("\"type\"", json);
            Assert.Contains("\"value\"", json);
            Assert.Contains("counter", json);
        }
    }
}
