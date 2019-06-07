using Microsoft.ApplicationInsights;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Extensions.Logging;
using SendGrid;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace appsvcbuild
{
    public static class HttpPythonPipeline
    {
        private static ILogger _log;
        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        public static async Task<String> Run(BuildRequest br, ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpPythonPipeline started");
            await InitUtils(log);

            LogInfo("HttpPythonPipeline request received");

            try
            {
                _mailUtils._buildRequest = br;
                LogInfo($"HttpPythonPipeline executed at: { DateTime.Now }");
                LogInfo(String.Format("new Python BuildRequest found {0}", br.ToString()));

                Boolean success = await MakePipeline(br, log);
                await _mailUtils.SendSuccessMail(new List<String> { br.Version }, GetLog());
                String successMsg =
                    $@"{{
                        ""status"": ""success"",
                        ""image"": ""appsvcbuildacr.azurecr.io/{br.OutputImageName}"",
                        ""webApp"": ""https://{br.WebAppName}.azurewebsites.net""
                    }}";
                return successMsg;
            }
            catch (Exception e)
            {
                LogInfo(e.ToString());
                _telemetry.TrackException(e);
                await _mailUtils.SendFailureMail(e.ToString(), GetLog());
                String failureMsg =
                    $@"{{
                        ""status"": ""failure"",
                        ""error"": ""{e.ToString()}""
                    }}";
                return failureMsg;
            }
        }

        public static void LogInfo(String message)
        {
            _emailLog.Append(message);
            _log.LogInformation(message);
            _telemetry.TrackEvent(message);
        }
        public static String GetLog()
        {
            return _emailLog.ToString();
        }

        public static async System.Threading.Tasks.Task InitUtils(ILogger log)
        {
            _emailLog = new StringBuilder();
            _secretsUtils = new SecretsUtils();
            await _secretsUtils.GetSecrets();
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Python");
            _dockerhubUtils = new DockerhubUtils();
            _githubUtils = new GitHubUtils(_secretsUtils._gitToken);
            _pipelineUtils = new PipelineUtils(
                new ContainerRegistryManagementClient(_secretsUtils._credentials),
                new WebSiteManagementClient(_secretsUtils._credentials),
                _secretsUtils._subId
                );

            _log = log;
            _mailUtils._log = log;
            _dockerhubUtils._log = log;
            _githubUtils._log = log;
            _pipelineUtils._log = log;
        }

        public static async Task<Boolean> MakePipeline(BuildRequest br, ILogger log)
        {
            int tries = br.Tries;
            while (true)
            {
                try
                {
                    tries--;
                    _mailUtils._version = br.Version;
                    LogInfo("Creating pipeline for Python " + br.Version);
                    await PushGithubAsync(br);
                    await CreatePythonHostingStartPipeline(br);
                    await PushGithubAppAsync(br);
                    await CreatePythonAppPipeline(br);
                    LogInfo(String.Format("Python {0} built", br.Version));
                    return true;
                }
                catch (Exception e)
                {
                    LogInfo(e.ToString());
                    if (tries <= 0)
                    {
                        LogInfo(String.Format("Python {0} failed", br.Version));
                        throw e;
                    }
                    LogInfo("trying again");
                    System.Threading.Thread.Sleep(1 * 60 * 1000);  //1 min
                }
            }
        }

        public static async Task<Boolean> CreatePythonHostingStartPipeline(BuildRequest br)
        {
            LogInfo("creating pipeling for python hostingstart " + br.Version);

            String pythonVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-python-hostingstart-{0}-task", pythonVersionDash);
            String planName = "appsvcbuild-python-plan";

            LogInfo("creating acr task for python hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, _secretsUtils._gitToken, br.OutputImageName, _secretsUtils._pipelineToken);
            LogInfo("done creating acr task for python hostingstart " + br.Version);

            LogInfo("creating webapp for python hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, _secretsUtils._acrPassword, br.WebAppName, br.OutputImageName, planName);
            LogInfo("done creating webapp for python hostingstart " + br.Version);

            return true;
        }

        public static async Task<Boolean> CreatePythonAppPipeline(BuildRequest br)
        {
            LogInfo("Creating pipeline for Python app " + br.Version);

            String pythonVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("appsvcbuild-python-app-{0}-task", pythonVersionDash);
            String planName = "appsvcbuild-python-app-plan";

            LogInfo("Creating acr task for Python app" + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.TestOutputRepoURL, _secretsUtils._gitToken, br.TestOutputImageName, _secretsUtils._pipelineToken);
            LogInfo("done creating acr task for python app" + br.Version);

            LogInfo("Creating webapp for Python app" + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, _secretsUtils._acrPassword, br.TestWebAppName, br.TestOutputImageName, planName);
            LogInfo("Done creating webapp for Python app" + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAsync(BuildRequest br)
        {
            LogInfo("Creating github files for Python " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("D:\\local\\Temp\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.TemplateRepoBranchName, br.PullRepo, br.PullId);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.OutputRepoOrgName, br.OutputRepoName))
            {
                _githubUtils.Clone(
                    br.OutputRepoURL,
                    localOutputRepoPath,
                    br.OutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.OutputRepoOrgName, br.OutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.OutputRepoOrgName, br.OutputRepoName);
            }
            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String> { String.Format("FROM {0}", br.BaseImageName) },
                new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.OutputRepoBranchName, String.Format("[appsvcbuild] Add python {0}", br.Version));
            _githubUtils.gitDispose(localOutputRepoPath);
            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);
            LogInfo("Done creating github files for Python " + br.Version);

            return true;
        }

        private static async Task<Boolean> PushGithubAppAsync(BuildRequest br)
        {
            LogInfo("Creating github files for Python app" + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("D:\\local\\Temp\\appsvcbuild{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TestTemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.TestOutputRepoName);

            _githubUtils.Clone(br.TestTemplateRepoURL, localTemplateRepoPath, br.TestTemplateRepoBranchName);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.TestOutputRepoOrgName, br.TestOutputRepoName))
            {
                _githubUtils.Clone(
                    br.TestOutputRepoURL,
                    localOutputRepoPath,
                    br.TestOutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.TestOutputRepoOrgName, br.TestOutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.TestOutputRepoOrgName, br.TestOutputRepoName);
            }

            _githubUtils.DeepCopy(
                 String.Format("{0}\\{1}", localTemplateRepoPath, br.TestTemplateName),
                localOutputRepoPath);
            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String>{ String.Format("FROM appsvcbuildacr.azurecr.io/{0}", br.TestBaseImageName) },
                new List<int> { 1 });

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.TestOutputRepoBranchName, String.Format("[appsvcbuild] Add python {0}", br.Version));
            _githubUtils.gitDispose(localOutputRepoPath);
            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);
            LogInfo("Done creating github files for Python app" + br.Version);

            return true;
        }
    }
}
