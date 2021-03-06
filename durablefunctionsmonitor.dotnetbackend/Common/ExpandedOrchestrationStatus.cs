using System;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using System.Text.RegularExpressions;
using Microsoft.WindowsAzure.Storage.Table;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace DurableFunctionsMonitor.DotNetBackend
{
    public enum EntityTypeEnum
    {
        Orchestration = 0,
        DurableEntity
    }

    // Adds extra fields to original DurableOrchestrationStatus
    public class ExpandedOrchestrationStatus : DurableOrchestrationStatus
    {
        public static readonly Regex EntityIdRegex = new Regex(@"@(\w+)@(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public EntityTypeEnum EntityType { get; private set; }
        public EntityId? EntityId { get; private set; }

        public string LastEvent
        {
            get
            {
                if(this._detailsTask == null)
                {
                    return string.Empty;
                }

                if (this._lastEvent != null)
                {
                    return this._lastEvent;
                }

                this._lastEvent = string.Empty;


                DurableOrchestrationStatus details;
                try
                {
                    // For some orchestrations getting an extended status might fail due to bugs in DurableOrchestrationClient.
                    // So just returning an empty string in that case.
                    details = this._detailsTask.Result;
                }
                catch(Exception)
                {
                    return this._lastEvent;
                }

                if (details.History == null)
                {
                    return this._lastEvent;
                }

                var lastEvent = details.History
                    .Select(e => e["Name"] ?? e["FunctionName"] )
                    .LastOrDefault(e => e != null);

                if (lastEvent == null)
                {
                    return this._lastEvent;
                }

                this._lastEvent = lastEvent.ToString();
                return this._lastEvent;
            }
        }
        public ExpandedOrchestrationStatus(DurableOrchestrationStatus that, 
            Task<DurableOrchestrationStatus> detailsTask,
            Task<IEnumerable<HistoryEntity>> subOrchestrationsTask)
        {
            this.Name = that.Name;
            this.InstanceId = that.InstanceId;
            this.CreatedTime = that.CreatedTime;
            this.LastUpdatedTime = that.LastUpdatedTime;
            this.Input = that.Input;
            this.Output = that.Output;
            this.RuntimeStatus = that.RuntimeStatus;
            this.CustomStatus = that.CustomStatus;

            this.History = subOrchestrationsTask == null ? that.History : this.TryMatchingSubOrchestrations(that.History, subOrchestrationsTask);

            // Detecting whether it is an Orchestration or a Durable Entity
            var match = EntityIdRegex.Match(this.InstanceId);
            if(match.Success)
            {
                this.EntityType = EntityTypeEnum.DurableEntity;
                this.EntityId = new EntityId(match.Groups[1].Value, match.Groups[2].Value);
            }

            this._detailsTask = detailsTask;
        }
        private Task<DurableOrchestrationStatus> _detailsTask;
        private string _lastEvent;

        private static readonly string[] SubOrchestrationEventTypes = new[] 
        {
            "SubOrchestrationInstanceCompleted",
            "SubOrchestrationInstanceFailed",
        };

        private JArray TryMatchingSubOrchestrations(JArray history, Task<IEnumerable<HistoryEntity>> subOrchestrationsTask)
        {
            if(history == null)
            {
                return null;
            }

            var subOrchestrationEvents = history
                .Where(h => SubOrchestrationEventTypes.Contains(h.Value<string>("EventType")))
                .ToList();

            if(subOrchestrationEvents.Count <= 0)
            {
                return history;
            }

            try
            {
                foreach (var subOrchestration in subOrchestrationsTask.Result)
                {
                    // Trying to match by SubOrchestration name and start time
                    var matchingEvent = subOrchestrationEvents.FirstOrDefault(e =>
                        e.Value<string>("FunctionName") == subOrchestration.Name &&
                        e.Value<DateTime>("ScheduledTime") == subOrchestration._Timestamp
                    );

                    if (matchingEvent == null)
                    {
                        continue;
                    }

                    // Modifying the event object
                    matchingEvent["SubOrchestrationId"] = subOrchestration.InstanceId;

                    // Dropping this line, so that multiple suborchestrations are correlated correctly
                    subOrchestrationEvents.Remove(matchingEvent);
                }
            } 
            catch(Exception ex)
            {
                // Intentionally swallowing any exceptions here
            }

            return history;
        }
    }

    // Represents an record in XXXHistory table
    public class HistoryEntity : TableEntity
    {
        public string InstanceId { get; set; }
        public string Name { get; set; }
        public DateTimeOffset _Timestamp { get; set; }
    }
}