using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Indubit.IssueApprovalWorkflow.Core.Models
{

    public enum ApprovalStatus
    {
        Pending,
        Approved,
        Rejected
    }

    public class ApprovalRequest
    {
        public string RequestId { get; set; }
        public string IncidentId { get; set; }
        public string Description { get; set; }
        public string Action { get; set; }
        public Dictionary<string, string> ActionParameters { get; set; }
        public List<string> ApproverEmails { get; set; }
        public DateTime CreatedAt { get; set; }
        public ApprovalStatus Status { get; set; }
    }

    public class ApprovalResponse
    {
        public string RequestId { get; set; }
        public string Action { get; set; }
        public DateTime ResponseTime { get; set; }
        public string ApproverEmail { get; set; }
    }

    public class TokenData
    {
        public string Token { get; set; }
        public string RequestId { get; set; }
        public string Action { get; set; }
        public string ApproverEmail { get; set; }
        public DateTime ExpirationDate { get; set; }
        public string TokenStatus { get; set; }
    }
}
