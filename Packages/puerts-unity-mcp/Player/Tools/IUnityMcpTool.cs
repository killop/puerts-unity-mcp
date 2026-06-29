using System;
using System.Threading.Tasks;

namespace PuertsUnityMcp
{
    public interface IUnityMcpTool
    {
        string Name { get; }
        string Description { get; }
        string InputSchemaJson { get; }
        Task<string> ExecuteAsync(UnityMcpToolContext context, UnityMcpToolArguments arguments);
    }

    public sealed class UnityMcpToolContext
    {
        public UnityMcpToolContext(IUnityMcpEndpoint endpoint)
        {
            Endpoint = endpoint;
        }

        public IUnityMcpEndpoint Endpoint { get; }
        public string EndpointId => Endpoint.EndpointId;
        public string EndpointKind => Endpoint.EndpointKind;
    }

    public sealed class DelegateUnityMcpTool : IUnityMcpTool
    {
        private readonly Func<UnityMcpToolContext, UnityMcpToolArguments, Task<string>> executeAsync;

        public DelegateUnityMcpTool(
            string name,
            string description,
            string inputSchemaJson,
            Func<UnityMcpToolContext, UnityMcpToolArguments, Task<string>> executeAsync)
        {
            Name = name;
            Description = description;
            InputSchemaJson = string.IsNullOrEmpty(inputSchemaJson) ? JsonSchemas.Object() : inputSchemaJson;
            this.executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        }

        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson { get; }

        public Task<string> ExecuteAsync(UnityMcpToolContext context, UnityMcpToolArguments arguments)
        {
            return executeAsync(context, arguments ?? new UnityMcpToolArguments());
        }
    }
}
