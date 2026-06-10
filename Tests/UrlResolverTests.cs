using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics.CodeAnalysis;

namespace Talktastic.Tests;

public sealed class UrlResolverTests
{
	private static readonly string[] SingleIntermediateUrl = ["A"];
	private static readonly string[] ChainedIntermediateUrls = ["A", "B"];
	private static readonly string[] MaxDepthIntermediateUrls = ["A", "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9"];
	private static readonly string[] CollectedNames = ["foo", "bar"];
	private static readonly string[] SingleName = ["bar"];

	[Theory]
	[InlineData("http://example.com/file.bin", true)]
	[InlineData("https://example.com/file.bin", true)]
	[InlineData("file:///C:/temp/file.bin", false)]
	[InlineData("ftp://example.com/file.bin", false)]
	[InlineData("definitely not a url", false)]
	public void HttpRedirectResolver_CanResolve_ReturnsExpectedResult
	(
		string url,
		bool expected
	)
	{
		var resolver = new HttpRedirectResolver();

		var canResolve = resolver.CanResolve(url);

		Assert.Equal(expected, canResolve);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_NoRedirect_ReturnsOriginalUrl()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse(HttpStatusCode.OK, request)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/model.onnx", result.Url);
		Assert.Null(result.DisplayName);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_Redirect_ReturnsRedirectedUrl()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse
			(
				HttpStatusCode.Redirect,
				request,
				location: new Uri("https://example.com/final/model.onnx")
			)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/original/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/final/model.onnx", result.Url);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_ContentDisposition_ReturnsDisplayName()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse
			(
				HttpStatusCode.OK,
				request,
				contentDisposition: new ContentDispositionHeaderValue("attachment")
				{
					FileName = "voice.onnx",
				}
			)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("voice.onnx", result.DisplayName);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_ContentDispositionFilenameStar_ReturnsDecodedDisplayName()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse
			(
				HttpStatusCode.OK,
				request,
				contentDisposition: new ContentDispositionHeaderValue("attachment")
				{
					FileNameStar = "Ryan Voice.onnx",
				}
			)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("Ryan Voice.onnx", result.DisplayName);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_ContentDispositionWithoutFilename_ReturnsNullDisplayName()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse
			(
				HttpStatusCode.OK,
				request,
				contentDisposition: new ContentDispositionHeaderValue("inline")
			)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Null(result.DisplayName);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_Head405_FallsBackToGet()
	{
		var methods = new List<HttpMethod>();
		using var http = CreateHttpClient
		(
			request =>
			{
				methods.Add(request.Method);

				return request.Method == HttpMethod.Head
					? CreateResponse(HttpStatusCode.MethodNotAllowed, request)
					: CreateResponse(HttpStatusCode.OK, request);
			}
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/model.onnx", result.Url);
		Assert.Collection
		(
			methods,
			method => Assert.Equal(HttpMethod.Head, method),
			method => Assert.Equal(HttpMethod.Get, method)
		);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_NullContentDisposition_ReturnsNullDisplayName()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse(HttpStatusCode.OK, request, contentDisposition: null)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Null(result.DisplayName);
	}

	[Fact]
	public async Task RunResolverChain_SingleResolverTransform_ReturnsTransformedUrl()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(("A", "B", null)),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal("B", result.FinalUrl);
		Assert.Equal(SingleIntermediateUrl, result.IntermediateUrls);
		Assert.Empty(result.Names);
	}

	[Fact]
	public async Task RunResolverChain_ChainOfResolvers_ReturnsFinalUrlAndIntermediates()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(("A", "B", null)),
			new TestResolver(("B", "C", null)),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal("C", result.FinalUrl);
		Assert.Equal(ChainedIntermediateUrls, result.IntermediateUrls);
	}

	[Fact]
	public async Task RunResolverChain_NoResolverMatches_ReturnsInputUrl()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(("B", "C", null)),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal("A", result.FinalUrl);
		Assert.Empty(result.IntermediateUrls);
		Assert.Empty(result.Names);
	}

	[Fact]
	public async Task RunResolverChain_CycleDetection_StopsBeforeRepeatingUrl()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver
			(
				("A", "B", null),
				("B", "A", null)
			),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal("B", result.FinalUrl);
		Assert.Equal(SingleIntermediateUrl, result.IntermediateUrls);
	}

	[Fact]
	public async Task RunResolverChain_MaxDepth_StopsAtConfiguredDepth()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(CreateMaxDepthMappings(10)),
		};

		var result = await RunResolverChain(resolvers, "A", http, maxDepth: 10);

		Assert.Equal("A10", result.FinalUrl);
		Assert.Equal(MaxDepthIntermediateUrls, result.IntermediateUrls);
	}

	[Fact]
	public async Task RunResolverChain_NamesCollectedFromEachStep_ReturnsAllNames()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(("A", "B", "foo")),
			new TestResolver(("B", "C", "bar")),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal(CollectedNames, result.Names);
	}

	[Fact]
	public async Task RunResolverChain_NullNamesFiltered_DoesNotCollectNullNames()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(("A", "B", null)),
			new TestResolver(("B", "C", "bar")),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal(SingleName, result.Names);
	}

	[Theory]
	[InlineData(HttpStatusCode.Moved)]
	[InlineData(HttpStatusCode.TemporaryRedirect)]
	[InlineData(HttpStatusCode.PermanentRedirect)]
	public async Task HttpRedirectResolver_ResolveAsync_AllRedirectCodes_ReturnsRedirectedUrl
	(
		HttpStatusCode statusCode
	)
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse
			(
				statusCode,
				request,
				location: new Uri("https://example.com/redirected/model.onnx")
			)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/original/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/redirected/model.onnx", result.Url);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_RelativeLocation_ResolvesRelatively()
	{
		using var http = CreateHttpClient
		(
			request => CreateResponse
			(
				HttpStatusCode.Redirect,
				request,
				location: new Uri("/other/path.onnx", UriKind.Relative)
			)
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/other/path.onnx", result.Url);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_RedirectWithoutLocation_ReturnsRequestUrl()
	{
		using var http = CreateHttpClient
		(
			request =>
			{
				var forwardedRequest = new HttpRequestMessage
				(
					request.Method,
					"https://example.com/final/model.onnx"
				);

				return new HttpResponseMessage(HttpStatusCode.Redirect)
				{
					RequestMessage = forwardedRequest,
					Content = new ByteArrayContent([]),
				};
			}
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/original/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/final/model.onnx", result.Url);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_RedirectWithoutLocationAndRequestMessage_ReturnsOriginalUrl()
	{
		using var http = CreateHttpClient
		(
			static _ => new HttpResponseMessage(HttpStatusCode.Redirect)
			{
				RequestMessage = null,
				Content = new ByteArrayContent([]),
			}
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/original/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/original/model.onnx", result.Url);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_RelativeLocationWithoutRequestUri_ReturnsOriginalUrl()
	{
		using var http = CreateHttpClient
		(
			static _ =>
			{
				var response = new HttpResponseMessage(HttpStatusCode.Redirect)
				{
					RequestMessage = null,
					Content = new ByteArrayContent([]),
				};
				response.Headers.Location = new Uri("/other/path.onnx", UriKind.Relative);
				return response;
			}
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/original/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/original/model.onnx", result.Url);
	}

	[Fact]
	public async Task HttpRedirectResolver_ResolveAsync_NullRequestMessage_FallsBackToOriginalUrl()
	{
		using var http = CreateHttpClient
		(
			request =>
			{
				var response = new HttpResponseMessage(HttpStatusCode.OK)
				{
					RequestMessage = null,
					Content = new ByteArrayContent([]),
				};
				return response;
			}
		);
		var resolver = new HttpRedirectResolver();

		var result = await resolver.ResolveAsync
		(
			http,
			"https://example.com/model.onnx",
			CancellationToken.None
		);

		Assert.Equal("https://example.com/model.onnx", result.Url);
	}

	[Fact]
	public async Task RunResolverChain_ResolverOrdering_FirstMatchingResolverWinsPerIteration()
	{
		using var http = CreateHttpClient(request => CreateResponse(HttpStatusCode.OK, request));
		var resolvers = new IUrlResolver[]
		{
			new TestResolver(("A", "B", null)),
			new TestResolver(("A", "C", null)),
		};

		var result = await RunResolverChain(resolvers, "A", http);

		Assert.Equal("B", result.FinalUrl);
		Assert.Equal(SingleIntermediateUrl, result.IntermediateUrls);
	}

	[SuppressMessage
	(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "HttpClient owns and disposes the handler via disposeHandler: true."
	)]
	private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
	{
		var messageHandler = CreateHttpMessageHandler(handler);
		return new HttpClient(messageHandler, disposeHandler: true);
	}

	private static MockHttpHandler CreateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
	{
		return new MockHttpHandler(handler);
	}

	private static HttpResponseMessage CreateResponse
	(
		HttpStatusCode statusCode,
		HttpRequestMessage request,
		Uri? location = null,
		ContentDispositionHeaderValue? contentDisposition = null
	)
	{
		var response = new HttpResponseMessage(statusCode)
		{
			RequestMessage = request,
			Content = new ByteArrayContent([]),
		};

		if (location is not null)
		{
			response.Headers.Location = location;
		}

		if (contentDisposition is not null)
		{
			response.Content.Headers.ContentDisposition = contentDisposition;
		}

		return response;
	}

	private static async Task<(string FinalUrl, List<string> IntermediateUrls, List<string> Names)> RunResolverChain
	(
		IReadOnlyList<IUrlResolver> resolvers,
		string inputUrl,
		HttpClient http,
		int maxDepth = 10,
		CancellationToken ct = default
	)
	{
		var currentUrl = inputUrl;
		var intermediateUrls = new List<string>();
		var names = new List<string>();
		var seenUrls = new HashSet<string>(StringComparer.Ordinal)
		{
			inputUrl,
		};

		for (var depth = 0; depth < maxDepth; depth++)
		{
			var changed = false;

			foreach (var resolver in resolvers)
			{
				if (!resolver.CanResolve(currentUrl))
				{
					continue;
				}

				var result = await resolver.ResolveAsync(http, currentUrl, ct).ConfigureAwait(false);

				if (string.Equals(result.Url, currentUrl, StringComparison.Ordinal))
				{
					continue;
				}

				if (!seenUrls.Add(result.Url))
				{
					return (currentUrl, intermediateUrls, names);
				}

				intermediateUrls.Add(currentUrl);

				if (result.DisplayName is not null)
				{
					names.Add(result.DisplayName);
				}

				currentUrl = result.Url;
				changed = true;
				break;
			}

			if (!changed)
			{
				break;
			}
		}

		return (currentUrl, intermediateUrls, names);
	}

	private static (string Input, string Output, string? Name)[] CreateMaxDepthMappings(int maxDepth)
	{
		var mappings = new (string Input, string Output, string? Name)[maxDepth];

		for (var i = 0; i < maxDepth; i++)
		{
			var input = i == 0 ? "A" : $"A{i}";
			var output = $"A{i + 1}";
			mappings[i] = (input, output, null);
		}

		return mappings;
	}

	private sealed class TestResolver : IUrlResolver
	{
		private readonly Dictionary<string, UrlResolverResult> _mappings;

		public TestResolver(params (string Input, string Output, string? Name)[] mappings)
		{
			_mappings = mappings.ToDictionary
			(
				mapping => mapping.Input,
				mapping => new UrlResolverResult(mapping.Output, mapping.Name),
				StringComparer.Ordinal
			);
		}

		public bool CanResolve(string url)
		{
			return _mappings.ContainsKey(url);
		}

		public Task<UrlResolverResult> ResolveAsync
		(
			HttpClient http,
			string url,
			CancellationToken ct
		)
		{
			return Task.FromResult(_mappings[url]);
		}
	}

	private sealed class MockHttpHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

		public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
		{
			_handler = handler;
		}

		protected override Task<HttpResponseMessage> SendAsync
		(
			HttpRequestMessage request,
			CancellationToken ct
		)
		{
			return Task.FromResult(_handler(request));
		}
	}
}
