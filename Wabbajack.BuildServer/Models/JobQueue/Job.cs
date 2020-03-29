﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Wabbajack.Lib.NexusApi;

namespace Wabbajack.BuildServer.Models.JobQueue
{ 
    public class Job
    {
        public enum JobPriority : int
        {
            Low,
            Normal,
            High,
        }

        public long Id { get; set; }
        public DateTime? Started { get; set; }
        public DateTime? Ended { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
        public JobPriority Priority { get; set; } = JobPriority.Normal;
        public JobResult Result { get; set; }
        public bool RequiresNexus { get; set; } = true;
        public AJobPayload Payload { get; set; }
        
        public Job OnSuccess { get; set; }

        public static async Task<Job> GetNext(DBContext db)
        {
            var filter = new BsonDocument
            {
                {"Started", BsonNull.Value}
            };
            var update = new BsonDocument
            {
                {"$set", new BsonDocument {{"Started", DateTime.Now}}}
            };
            var sort = new {Priority=-1, Created=1}.ToBsonDocument();
            var job = await db.Jobs.FindOneAndUpdateAsync<Job>(filter, update, new FindOneAndUpdateOptions<Job>{Sort = sort});
            return job;
        }

        public static async Task<Job> Finish(DBContext db, Job job, JobResult jobResult)
        {
            if (jobResult.ResultType == JobResultType.Success && job.OnSuccess != null)
            {
                await db.Jobs.InsertOneAsync(job.OnSuccess);
            }

            var filter = new BsonDocument
            {
                {"_id", job.Id},
            };
            var update = new BsonDocument
            {
                {"$set", new BsonDocument {{"Ended", DateTime.Now}, {"Result", jobResult.ToBsonDocument()}}}
            };
            var result = await db.Jobs.FindOneAndUpdateAsync<Job>(filter, update);
            return result;
        }
    }
}
