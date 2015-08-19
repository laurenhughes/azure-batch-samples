﻿namespace Microsoft.Azure.Batch.Samples.HelloWorld
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;
    using WindowsAzure.Storage;
    using WindowsAzure.Storage.Auth;
    using WindowsAzure.Storage.Blob;
    using Microsoft.Azure.Batch;
    using Microsoft.Azure.Batch.Auth;
    using Microsoft.Azure.Batch.Common;
    using Microsoft.Azure.Batch.FileStaging;
    using Constants = Microsoft.Azure.Batch.Constants;

    /// <summary>
    /// Manages submission and lifetime of the Azure Batch job and pool.
    /// </summary>
    public class JobSubmitter
    {
        private readonly Settings configurationSettings;

        public JobSubmitter()
        {
            this.configurationSettings = Settings.Default;
        }

        public JobSubmitter(Settings settings)
        {
            this.configurationSettings = settings;
        }

        /// <summary>
        /// Populates Azure Storage with the required files, and 
        /// submits the job to the Azure Batch service.
        /// </summary>
        public async Task RunAsync()
        {
            Console.WriteLine("Running with the following settings: ");
            Console.WriteLine("-------------------------------------");
            Console.WriteLine(this.configurationSettings.ToString());

            // Set up the Batch Service credentials used to authenticate with the Batch Service.
            BatchSharedKeyCredentials credentials = new BatchSharedKeyCredentials(
                this.configurationSettings.BatchServiceUrl,
                this.configurationSettings.BatchAccountName,
                this.configurationSettings.BatchAccountKey);

            // Get an instance of the BatchClient for a given Azure Batch account.
            using (BatchClient batchClient = await BatchClient.OpenAsync(credentials))
            {
                // add a retry policy. The built-in policies are No Retry (default), Linear Retry, and Exponential Retry
                batchClient.CustomBehaviors.Add(RetryPolicyProvider.LinearRetryProvider(TimeSpan.FromSeconds(10), 3));

                string jobId = null;
                HashSet<string> blobContainerNames = null;

                try
                {
                    // Allocate a pool
                    await this.CreatePoolIfNotExistAsync(batchClient);

                    // Submit the job
                    jobId = CreateJobId("HelloWorldJob");
                    blobContainerNames = await this.SubmitJobAsync(batchClient, jobId);

                    // Print out the status of the pools/jobs under this account
                    await ListJobsAsync(batchClient);
                    await ListPoolsAsync(batchClient);

                    // Wait for the job to complete
                    await this.WaitForJobAndPrintOutputAsync(batchClient, jobId);
                }
                finally
                {
                    // Delete the pool (if configured) and job
                    // TODO: In C# 6 we can await here instead of .Wait()
                    this.CleanupResourcesAsync(batchClient, jobId, blobContainerNames).Wait();
                }
            }
        }

        /// <summary>
        /// Creates a pool if it doesn't already exist.  If the pool already exists, this method resizes it to meet the expected
        /// targets specified in settings.
        /// </summary>
        /// <param name="batchClient">The BatchClient to use when interacting with the Batch service.</param>
        /// <returns>An asynchronous <see cref="Task"/> representing the operation.</returns>
        private async Task CreatePoolIfNotExistAsync(BatchClient batchClient)
        {
            bool successfullyCreatedPool = false;

            // Attempt to create the pool
            try
            {
                // Create an in-memory representation of the Batch pool which we would like to create.  We are free to modify/update 
                // this pool object in memory until we submit it to the service via a call to "Commit"
                Console.WriteLine("Attempting to create pool: {0}", this.configurationSettings.PoolId);

                // You can learn more about os families and versions at:
                // https://azure.microsoft.com/en-us/documentation/articles/cloud-services-guestos-update-matrix/
                CloudPool pool = batchClient.PoolOperations.CreatePool(
                    poolId: this.configurationSettings.PoolId,
                    targetDedicated: this.configurationSettings.PoolTargetNodeCount,
                    virtualMachineSize: this.configurationSettings.PoolNodeVirtualMachineSize,
                    osFamily: this.configurationSettings.PoolOSFamily);

                // Create the pool on the Batch Service
                await pool.CommitAsync();

                successfullyCreatedPool = true;
                Console.WriteLine("Created pool {0} with {1} {2} nodes",
                    pool,
                    this.configurationSettings.PoolTargetNodeCount,
                    this.configurationSettings.PoolNodeVirtualMachineSize);

            }
            catch (BatchException e)
            {
                if (e.RequestInformation != null &&
                    e.RequestInformation.AzureError != null &&
                    e.RequestInformation.AzureError.Code == BatchErrorCodeStrings.PoolExists)
                {
                    // The pool already existed when we tried to create it
                    successfullyCreatedPool = false;
                    Console.WriteLine("The pool already existed when we tried to create it");
                }
                else
                {
                    throw; // Any other exception is unexpected
                }
            }

            // If the pool already existed, make sure that its targets are correct
            if (!successfullyCreatedPool)
            {
                CloudPool existingPool = await batchClient.PoolOperations.GetPoolAsync(this.configurationSettings.PoolId);

                // If the pool doesn't have the right number of nodes and it isn't resizing then we need
                // to ask it to resize
                if (existingPool.CurrentDedicated != this.configurationSettings.PoolTargetNodeCount &&
                    existingPool.AllocationState != AllocationState.Resizing)
                {
                    // Resize the pool to the desired target.  Note that provisioning the nodes in the pool may take some time
                    await existingPool.ResizeAsync(this.configurationSettings.PoolTargetNodeCount);
                }
            }
        }

        /// <summary>
        /// Creates a job and adds 4 tasks to it.  2 tasks are basic with only a command line.  The other 2 tasks use 
        /// the file staging feature in order to add a resource file which each task consumes.
        /// </summary>
        /// <param name="batchClient">The BatchClient to use when interacting with the Batch service.</param>
        /// <param name="jobId">The ID of the job.</param>
        /// <returns>The set of container names containing the jobs input files.</returns>
        private async Task<HashSet<string>> SubmitJobAsync(BatchClient batchClient, string jobId)
        {
            // create an empty unbound Job
            CloudJob unboundJob = batchClient.JobOperations.CreateJob();
            unboundJob.Id = jobId;
            unboundJob.PoolInformation = new PoolInformation() { PoolId = this.configurationSettings.PoolId };

            // Commit Job to create it in the service
            await unboundJob.CommitAsync();

            // create 2 quick tasks. Each task within a job must have a unique ID
            List<CloudTask> tasksToRun = new List<CloudTask>();
            tasksToRun.Add(new CloudTask("task1", "hostname"));
            tasksToRun.Add(new CloudTask("task2", "cmd /c dir /s"));

            // Also create 2 tasks which require some resource files
            CloudTask taskWithFiles1 = new CloudTask("task_with_file1", "cmd /c type 2>nul *.txt");
            CloudTask taskWithFiles2 = new CloudTask("task_with_file2", "cmd /c dir /s");

            // Set up a collection of files to be staged -- these files will be uploaded to Azure Storage
            // when the tasks are submitted to the Azure Batch service.
            taskWithFiles1.FilesToStage = new List<IFileStagingProvider>();
            taskWithFiles2.FilesToStage = new List<IFileStagingProvider>();

            // generate a local file in temp directory
            string path = Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "HelloWorld.txt");
            File.WriteAllText(path, "hello from Batch HelloWorld sample!");

            // add the files as a task dependency so they will be uploaded to storage before the task 
            // is submitted and downloaded to the node before the task starts execution.
            FileToStage file = new FileToStage(path,
                new StagingStorageAccount(
                    storageAccount: this.configurationSettings.StorageAccountName,
                    storageAccountKey: this.configurationSettings.StorageAccountKey,
                    blobEndpoint: this.configurationSettings.StorageBlobEndpoint));

            taskWithFiles1.FilesToStage.Add(file);
            taskWithFiles2.FilesToStage.Add(file);

            tasksToRun.Add(taskWithFiles1);
            tasksToRun.Add(taskWithFiles2);

            var fileStagingArtifacts = new ConcurrentBag<ConcurrentDictionary<Type, IFileStagingArtifact>>();
            
            // Use the AddTask method which takes an enumerable of tasks for best performance, as it submits up to 100
            // tasks at once in a single request.  If the list of tasks is N where N > 100, this will correctly parallelize 
            // the requests and return when all N tasks have been added.
            await batchClient.JobOperations.AddTaskAsync(jobId, tasksToRun, fileStagingArtifacts: fileStagingArtifacts);

            // Extract the names of the blob containers from the file staging artifacts
            HashSet<string> blobContainerNames = ExtractBlobContainerNames(fileStagingArtifacts);
            return blobContainerNames;
        }

        /// <summary>
        /// Waits for all tasks under the specified job to complete and then prints each task's output to the console.
        /// </summary>
        /// <param name="batchClient">The BatchClient to use when interacting with the Batch service.</param>
        /// <param name="jobId">The ID of the job.</param>
        /// <returns>An asynchronous <see cref="Task"/> representing the operation.</returns>
        private async Task WaitForJobAndPrintOutputAsync(BatchClient batchClient, string jobId)
        {
            Console.WriteLine("Waiting for all tasks to complete on job: {0} ...", jobId);

            // We use the task state monitor to monitor the state of our tasks -- in this case we will wait for them all to complete.
            TaskStateMonitor taskStateMonitor = batchClient.Utilities.CreateTaskStateMonitor();

            // blocking wait on the list of tasks until all tasks reach completed state or the timeout is reached.
            // If the pool is being resized then enough time is needed for the nodes to reach the idle state in order
            // for tasks to run on them.
            List<CloudTask> ourTasks = await batchClient.JobOperations.ListTasks(jobId).ToListAsync();

            bool timedOut = await taskStateMonitor.WaitAllAsync(ourTasks, TaskState.Completed, TimeSpan.FromMinutes(10));

            if (timedOut)
            {
                throw new TimeoutException("Timed out waiting for tasks");
            }

            // dump task output
            foreach (CloudTask t in ourTasks)
            {
                Console.WriteLine("Task {0}", t.Id);

                //Read the standard out of the task
                NodeFile standardOutFile = await t.GetNodeFileAsync(Constants.StandardOutFileName);
                string standardOutText = await standardOutFile.ReadAsStringAsync();
                Console.WriteLine("Standard out:");
                Console.WriteLine(standardOutText);

                //Read the standard error of the task
                NodeFile standardErrorFile = await t.GetNodeFileAsync(Constants.StandardErrorFileName);
                string standardErrorText = await standardErrorFile.ReadAsStringAsync();
                Console.WriteLine("Standard error:");
                Console.WriteLine(standardErrorText);

                Console.WriteLine();
            }
        }

        /// <summary>
        /// Deletes the job and pool
        /// </summary>
        /// <param name="batchClient">The BatchClient to use when interacting with the Batch service.</param>
        /// <param name="jobId">The ID of the job.</param>
        /// <param name="blobContainerNames">The name of the containers created for the jobs resource files.</param>
        /// <returns>An asynchronous <see cref="Task"/> representing the operation.</returns>
        private async Task CleanupResourcesAsync(BatchClient batchClient, string jobId, IEnumerable<string> blobContainerNames)
        {
            // Delete the blob containers which contain the task input files since we no longer need them
            CloudStorageAccount storageAccount = new CloudStorageAccount(
                new StorageCredentials(this.configurationSettings.StorageAccountName,
                    this.configurationSettings.StorageAccountKey),
                    new Uri(this.configurationSettings.StorageBlobEndpoint),
                    null,
                    null,
                    null);

            CloudBlobClient cloudBlobClient = storageAccount.CreateCloudBlobClient();
            foreach (string blobContainerName in blobContainerNames)
            {
                CloudBlobContainer container = cloudBlobClient.GetContainerReference(blobContainerName);
                Console.WriteLine("Deleting container: {0}", blobContainerName);
                
                await container.DeleteAsync();
            }

            // Delete the job to ensure the tasks are cleaned up
            if (!string.IsNullOrEmpty(jobId) && this.configurationSettings.ShouldDeleteJob)
            {
                Console.WriteLine("Deleting job: {0}", jobId);
                await batchClient.JobOperations.DeleteJobAsync(jobId);
            }

            if (this.configurationSettings.ShouldDeletePool)
            {
                Console.WriteLine("Deleting pool: {0}", this.configurationSettings.PoolId);
                await batchClient.PoolOperations.DeletePoolAsync(this.configurationSettings.PoolId);
            }
        }

        /// <summary>
        /// Extracts the name of the container from the file staging artifacts.
        /// </summary>
        /// <param name="fileStagingArtifacts">The file staging artifacts.</param>
        /// <returns>A set containing all containers created by file staging.</returns>
        private static HashSet<string> ExtractBlobContainerNames(ConcurrentBag<ConcurrentDictionary<Type, IFileStagingArtifact>> fileStagingArtifacts)
        {
            HashSet<string> result = new HashSet<string>();

            foreach (ConcurrentDictionary<Type, IFileStagingArtifact> artifactContainer in fileStagingArtifacts)
            {
                foreach (IFileStagingArtifact artifact in artifactContainer.Values)
                {
                    SequentialFileStagingArtifact sequentialStagingArtifact = artifact as SequentialFileStagingArtifact;
                    if (sequentialStagingArtifact != null)
                    {
                        result.Add(sequentialStagingArtifact.BlobContainerCreated);
                    }
                }
            }

            return result;
        }

        private static string CreateJobId(string prefix)
        {
            // a job is uniquely identified by its ID so your account name along with a timestamp is added as suffix
            return string.Format("{0}-{1}-{2}", prefix, Environment.GetEnvironmentVariable("USERNAME"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        /// <summary>
        /// Lists all the pools in the Batch account.
        /// </summary>
        /// <param name="batchClient">The BatchClient to use when interacting with the Batch service.</param>
        /// <returns>An asynchronous <see cref="Task"/> representing the operation.</returns>
        private static async Task ListPoolsAsync(BatchClient batchClient)
        {
            Console.WriteLine("Listing Pools");
            Console.WriteLine("=============");

            // Using optional select clause to return only the properties of interest. Makes query faster and reduces HTTP packet size impact
            IPagedEnumerable<CloudPool> pools = batchClient.PoolOperations.ListPools(new ODATADetailLevel(selectClause: "id,state,currentDedicated,vmSize"));

            await pools.ForEachAsync(pool =>
                {
                    Console.WriteLine("State of pool {0} is {1} and it has {2} nodes of size {3}", pool.Id, pool.State, pool.CurrentDedicated, pool.VirtualMachineSize);
                });
            Console.WriteLine("=============");
        }

        /// <summary>
        /// Lists all the jobs in the Batch account.
        /// </summary>
        /// <param name="batchClient">The BatchClient to use when interacting with the Batch service.</param>
        /// <returns>An asynchronous <see cref="Task"/> representing the operation.</returns>
        private static async Task ListJobsAsync(BatchClient batchClient)
        {
            Console.WriteLine("Listing Jobs");
            Console.WriteLine("============");

            IPagedEnumerable<CloudJob> jobs = batchClient.JobOperations.ListJobs(new ODATADetailLevel(selectClause: "id,state"));
            await jobs.ForEachAsync(job =>
                {
                    Console.WriteLine("State of job " + job.Id + " is " + job.State);
                });

            Console.WriteLine("============");
        }
    }
}
