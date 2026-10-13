// ── .NET Framework ───────────────────────────────────────────────────────────
global using System;
global using System.Collections.Generic;
global using System.ComponentModel;
global using System.Linq;
global using System.Reflection;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;

// ── Microsoft Agent Framework ─────────────────────────────────────────────────
// SOURCE: agent-framework.pdf — namespaces used in official C# examples
global using Microsoft.Agents.AI;               // AIAgent, ChatClientAgent, AgentSession
global using Microsoft.Agents.AI.Workflows;     // AgentWorkflowBuilder, RoundRobinGroupChatManager

// ── Microsoft.Extensions.AI ───────────────────────────────────────────────────
// AIFunctionFactory, IChatClient, ChatOptions, ChatMessage, ChatRole
// FunctionInvocationContext (middleware), AIFunction
global using Microsoft.Extensions.AI;


// ── .NET Extensions ───────────────────────────────────────────────────────────
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Options;

// ── MentorAgent ──────────────────────────────────────────────────────────────
global using Novior.MentorAgent.Attributes;
global using Novior.MentorAgent.Data;       // MemoryEntry (server-only)
global using Novior.MentorAgent.Models;     // MentorMcpServer, MentorRemoteAgent, AgentCardInfo (server-only)
global using Novior.MentorAgent.Options;
global using Novior.MentorAgent.Utility;

// ── Novior.MentorAgent.Abstractions — shared interfaces, models, enums, localizer ───
global using Novior.MentorAgent.Abstractions.Core;
global using Novior.MentorAgent.Abstractions.Interfaces;
global using Novior.MentorAgent.Abstractions.Models;
