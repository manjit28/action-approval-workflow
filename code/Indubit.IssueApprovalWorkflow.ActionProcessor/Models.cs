using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Indubit.IssueApprovalWorkflow.ActionProcessor
{
    public class WorkerOptions
    {
        public string QueueUrl { get; set; } = string.Empty;
        public string ApprovalTableName { get; set; } = string.Empty;
        public int PollingIntervalSeconds { get; set; } = 30;
        public int MaxNumberOfMessages { get; set; } = 10;
        public int VisibilityTimeout { get; set; } = 300;
        public int WaitTimeSeconds { get; set; } = 20;
    }

    public class ApprovalMessage
    {
        public string RequestId { get; set; } = string.Empty;
        public string ApproverEmail { get; set; } = string.Empty;
        public string ApprovedAt { get; set; } = string.Empty;
        public string? Comment { get; set; }
        public ProposedAction ProposedAction { get; set; } = new();
    }

    public class ProposedAction
    {
        public string Action { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
