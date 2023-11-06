﻿using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AICentral.PipelineComponents.Endpoints.OpenAI;

public class OpenAIEndpointDispatcher : IAICentralEndpointDispatcher
{
    const string OpenAIV1 = "https://api.openai.com/v1";
    private readonly string _id;
    private readonly Dictionary<string, string> _modelMappings;
    private readonly string? _organization;
    private readonly OpenAIAuthHandler _authHandler;

    public OpenAIEndpointDispatcher(string id,
        Dictionary<string, string> modelMappings,
        string apiKey, 
        string? organization)
    {
        _id = id;
        _modelMappings = modelMappings;
        _organization = organization;
        _authHandler = new OpenAIAuthHandler(apiKey);
    }

    public async Task<(AICentralRequestInformation, HttpResponseMessage)> Handle(HttpContext context,
        AICentralPipelineExecutor pipeline, CancellationToken cancellationToken)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<OpenAIEndpointDispatcherBuilder>>();
        var typedDispatcher = context.RequestServices
            .GetRequiredService<ITypedHttpClientFactory<HttpAIEndpointDispatcher>>().CreateClient(
                context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(_id));

        context.Request.EnableBuffering(); //we may need to re-read the request if it fails.
        context.Request.Body.Position = 0;

        using var
            requestReader =
                new StreamReader(context.Request.Body,
                    leaveOpen: true); //leave open in-case we need to re-read it. TODO, optimise this and read it once.

        var requestRawContent = await requestReader.ReadToEndAsync(cancellationToken);
        var deserializedRequestContent = (JObject)JsonConvert.DeserializeObject(requestRawContent)!;

        var extractor = new AzureOpenAiCallInformationExtractor();
        var callInformation = extractor.Extract(context.Request, deserializedRequestContent);

        deserializedRequestContent["model"] = _modelMappings[callInformation.IncomingModelName];
        var updatedRequestRawContent = deserializedRequestContent.ToString(Formatting.None);

        var mappedModelName = _modelMappings.TryGetValue(callInformation.IncomingModelName, out var mapping)
            ? mapping
            : string.Empty;

        if (mappedModelName == string.Empty)
        {
            return (
                new AICentralRequestInformation(
                    OpenAIV1,
                    callInformation.AICallType,
                    callInformation.PromptText,
                    DateTimeOffset.Now,
                    TimeSpan.Zero
                ), new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var newUri = $"{OpenAIV1}/{callInformation.RemainingUrl}";
        
        logger.LogDebug(
            "Rewritten URL from {OriginalUrl} to {NewUrl}. Incoming Model: {IncomingModelName}. Mapped Model: {MappedModelName}",
            context.Request.GetEncodedUrl(),
            newUri,
            callInformation.IncomingModelName,
            mappedModelName);

        var now = DateTimeOffset.Now;
        var sw = new Stopwatch();

        sw.Start();
        var openAiResponse = await typedDispatcher.Dispatch(context, newUri, updatedRequestRawContent, _authHandler, cancellationToken);

        //this will retry the operation for retryable status codes. When we reach here we might not want
        //to stream the response if it wasn't a 200.
        sw.Stop();

        //decision point... If this is a streaming request, then we should start streaming the result now.
        logger.LogDebug("Received Azure Open AI Response. Status Code: {StatusCode}", openAiResponse.StatusCode);

        var requestInformation =
            new AICentralRequestInformation(OpenAIV1, callInformation.AICallType, callInformation.PromptText, now,
                sw.Elapsed);

        return (requestInformation, openAiResponse);
    }

    public object WriteDebug()
    {
        return new
        {
            Type = "OpenAI",
            Url = OpenAIV1,
            ModelMappings = _modelMappings,
            Auth = _authHandler.WriteDebug()
        };
    }

    internal class OpenAIAuthHandler : IEndpointAuthorisationHandler
    {
        private readonly string _apiKey;

        public OpenAIAuthHandler(string apiKey)
        {
            _apiKey = apiKey;
        }

        public Task ApplyAuthorisationToRequest(HttpRequest incomingRequest, HttpRequestMessage outgoingRequest)
        {
            outgoingRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return Task.CompletedTask;
        }

        public object WriteDebug()
        {
            return new { };
        }
    }
}