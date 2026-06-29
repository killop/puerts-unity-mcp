using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PuertsUnityMcp
{
    public sealed class UnityMcpToolRegistry
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<string, IUnityMcpTool> tools = new Dictionary<string, IUnityMcpTool>(StringComparer.Ordinal);

        public int Version { get; private set; }

        public void Register(IUnityMcpTool tool)
        {
            if (tool == null || string.IsNullOrEmpty(tool.Name))
            {
                throw new ArgumentException("Tool and tool name are required.");
            }

            lock (syncRoot)
            {
                tools[tool.Name] = tool;
                Version++;
            }
        }

        public bool Unregister(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            lock (syncRoot)
            {
                if (!tools.Remove(name))
                {
                    return false;
                }

                Version++;
                return true;
            }
        }

        public IReadOnlyList<UnityMcpToolDescriptor> List()
        {
            lock (syncRoot)
            {
                return tools.Values
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .Select(t => new UnityMcpToolDescriptor
                    {
                        name = t.Name,
                        description = t.Description,
                        inputSchemaJson = t.InputSchemaJson
                    })
                    .ToList();
            }
        }

        public async Task<string> ExecuteAsync(UnityMcpToolContext context, string name, UnityMcpToolArguments arguments)
        {
            IUnityMcpTool tool;
            lock (syncRoot)
            {
                if (!tools.TryGetValue(name, out tool))
                {
                    throw new InvalidOperationException("Unknown MCP tool: " + name);
                }
            }

            return await tool.ExecuteAsync(context, arguments ?? new UnityMcpToolArguments());
        }
    }
}
