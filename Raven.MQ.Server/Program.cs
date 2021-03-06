using System;
using System.Configuration.Install;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.ServiceProcess;
using log4net.Appender;
using log4net.Config;
using log4net.Filter;
using log4net.Layout;
using Raven.Http;
using RavenMQ.Config;
using RavenMQ.Extensions;
using RavenMQ.Impl;
using RavenMQ.Server.Responders;

namespace Raven.MQ.Server
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            if (Environment.UserInteractive)
            {
                try
                {
                    InteractiveRun(args);
                }
                catch (ReflectionTypeLoadException e)
                {
                    Console.WriteLine(e);
                    foreach (var loaderException in e.LoaderExceptions)
                    {
                        Console.WriteLine("- - - -");
                        Console.WriteLine(loaderException);
                    }
                    Environment.Exit(-1);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    Environment.Exit(-1);
                }
            }
            else
            {
                // no try catch here, we want the exception to be logged by Windows
                ServiceBase.Run(new RavenService());
            }
        }

        private static void InteractiveRun(string[] args)
        {
            switch (GetArgument(args))
            {
                case "install":
                    AdminRequired(InstallAndStart, "/install");
                    break;
                case "uninstall":
                    AdminRequired(EnsureStoppedAndUninstall, "/uninstall");
                    break;
                case "start":
                    AdminRequired(StartService, "/start");
                    break;
                case "restart":
                    AdminRequired(RestartService, "/restart");
                    break;
                case "stop":
                    AdminRequired(StopService, "/stop");
                    break;
                case "debug":
                    RunInDebugMode(anonymousUserAccessMode: null, ravenConfiguration: new RavenConfiguration());
                    break;
                case "ram":
                    RunInDebugMode(anonymousUserAccessMode: AnonymousUserAccessMode.All, ravenConfiguration: new RavenConfiguration
                    {
                        RunInMemory = true,
                    });
                    break;
#if DEBUG
                case "test":
                    var dataDirectory = new RavenConfiguration().DataDirectory;
                    IOExtensions.DeleteDirectory(dataDirectory);

                    RunInDebugMode(anonymousUserAccessMode: AnonymousUserAccessMode.All, ravenConfiguration: new RavenConfiguration());
                    break;
#endif
                default:
                    PrintUsage();
                    break;
            }
        }

        private static void AdminRequired(Action actionThatMayRequiresAdminPrivileges, string cmdLine)
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (principal.IsInRole(WindowsBuiltInRole.Administrator) == false)
            {
                if (RunAgainAsAdmin(cmdLine))
                    return;
            }
            actionThatMayRequiresAdminPrivileges();
        }

        private static bool RunAgainAsAdmin(string cmdLine)
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    Arguments = cmdLine,
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Verb = "runas",
                });
                if (process != null)
                    process.WaitForExit();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string GetArgument(string[] args)
        {
            if (args.Length == 0)
                return "debug";
            if (args[0].StartsWith("/") == false)
                return "help";
            return args[0].Substring(1);
        }

        private static void RunInDebugMode(Raven.Http.AnonymousUserAccessMode? anonymousUserAccessMode, RavenConfiguration ravenConfiguration)
        {
			var consoleAppender = new ConsoleAppender
			{
				Layout = new PatternLayout(PatternLayout.DefaultConversionPattern),
			};
			consoleAppender.AddFilter(new LoggerMatchFilter
			{
				AcceptOnMatch = true,
				LoggerToMatch = typeof(HttpServer).FullName
			});
			consoleAppender.AddFilter(new DenyAllFilter());
			BasicConfigurator.Configure(consoleAppender);
            NonAdminHttp.EnsureCanListenToWhenInNonAdminContext(ravenConfiguration.Port);
            if (anonymousUserAccessMode.HasValue)
                ravenConfiguration.AnonymousUserAccessMode = anonymousUserAccessMode.Value;
            using (new RavenMqServer(ravenConfiguration))
            {

				Console.WriteLine("RavenMQ is ready to process requests. Build {0}, Version {1}", Queues.BuildVersion, Queues.ProductVersion);
				Console.WriteLine("Data directory: {0}", ravenConfiguration.DataDirectory);
				Console.WriteLine("HostName: {0} Port: {1}, Storage: Munin", ravenConfiguration.HostName ?? "<any>",
					ravenConfiguration.Port);
				Console.WriteLine("Server Url: {0}", ravenConfiguration.ServerUrl);
				Console.WriteLine("Press <enter> to stop or 'cls' and <enter> to clear the log");
				while (true)
                {
                    var readLine = Console.ReadLine();
                    if (!"CLS".Equals(readLine, StringComparison.InvariantCultureIgnoreCase))
                        break;
                    Console.Clear();
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                @"
Raven MQ
Queuing System for the .Net Platform
----------------------------------------
Copyright (C) 2010 - Hibernating Rhinos
----------------------------------------
Command line ptions:
    Raven.MQ.Server             - with no args, starts Raven in local server mode
    Raven.MQ.Server /install    - installs and starts the Raven service
    Raven.MQ.Server /uninstall  - stops and uninstalls the Raven service
    Raven.MQ.Server /start		- starts the previously installed Raven service
    Raven.MQ.Server /stop		- stops the previously installed Raven service
    Raven.MQ.Server /restart	- restarts the previously installed Raven service

Enjoy...
");
        }

        private static void EnsureStoppedAndUninstall()
        {
            if (ServiceIsInstalled() == false)
            {
                Console.WriteLine("Service is not installed");
            }
            else
            {
                var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

                if (stopController.Status == ServiceControllerStatus.Running)
                    stopController.Stop();

                ManagedInstallerClass.InstallHelper(new[] { "/u", Assembly.GetExecutingAssembly().Location });
            }
        }

        private static void StopService()
        {
            var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

            if (stopController.Status == ServiceControllerStatus.Running)
            {
                stopController.Stop();
                stopController.WaitForStatus(ServiceControllerStatus.Stopped);
            }
        }


        private static void StartService()
        {
            var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

            if (stopController.Status != ServiceControllerStatus.Running)
            {
                stopController.Start();
                stopController.WaitForStatus(ServiceControllerStatus.Running);
            }
        }

        private static void RestartService()
        {
            var stopController = new ServiceController(ProjectInstaller.SERVICE_NAME);

            if (stopController.Status == ServiceControllerStatus.Running)
            {
                stopController.Stop();
                stopController.WaitForStatus(ServiceControllerStatus.Stopped);
            }
            if (stopController.Status != ServiceControllerStatus.Running)
            {
                stopController.Start();
                stopController.WaitForStatus(ServiceControllerStatus.Running);
            }

        }

        private static void InstallAndStart()
        {
            if (ServiceIsInstalled())
            {
                Console.WriteLine("Service is already installed");
            }
            else
            {
                ManagedInstallerClass.InstallHelper(new[] { Assembly.GetExecutingAssembly().Location });
                var startController = new ServiceController(ProjectInstaller.SERVICE_NAME);
                startController.Start();
            }
        }

        private static bool ServiceIsInstalled()
        {
            return (ServiceController.GetServices().Count(s => s.ServiceName == ProjectInstaller.SERVICE_NAME) > 0);
        }
    }
}
