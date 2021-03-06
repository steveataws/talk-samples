using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.AutoScaling;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.CodeDeploy;
using Amazon.CDK.AWS.CodePipeline;
using Amazon.CDK.AWS.CodePipeline.Actions;
using Amazon.CDK.AWS.CloudTrail;
using Amazon.CDK.AWS.S3;

namespace WindowsWebServer
{
    public class WindowsWebServerStack : Stack
    {
        const string DeploymentApplicationNameContextKey = "DeploymentApplicationName";
        const string DeploymentGroupNameContextKey = "DeploymentGroupName";
        const string AppBundleNameContextKey = "AppBundleName";

        internal WindowsWebServerStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            #region Application hosting resources

            // Create a new vpc, with one public and one private subnet per AZ, to host the fleet
            var vpc = new Vpc(this, "WindowsWebServerFleetVpc", new VpcProps
            {
                Cidr = "10.0.0.0/16",
                MaxAzs = 2,
                SubnetConfiguration = new SubnetConfiguration[]
                {
                    new SubnetConfiguration
                    {
                        CidrMask = 24,
                        SubnetType = SubnetType.PUBLIC,
                        Name = "PublicIngress"
                    },
                    new SubnetConfiguration
                    {
                        CidrMask = 24,
                        SubnetType = SubnetType.PRIVATE,
                        Name = "Private"
                    }
                }
            });

            // Define an auto scaling group for our instances, which will be placed into
            // private subnets by default
            var scalingGroup = new AutoScalingGroup(this, "WindowsWebServerFleetASG", new AutoScalingGroupProps
            {
                Vpc = vpc,
                InstanceType = InstanceType.Of(InstanceClass.BURSTABLE3, InstanceSize.MEDIUM),
                // since we don't hold any data on the instance, and install what we need on startup,
                // we just always launch the latest version
                MachineImage = MachineImage.LatestWindows(WindowsVersion.WINDOWS_SERVER_2019_ENGLISH_CORE_BASE),
                MinCapacity = 1,
                MaxCapacity = 4,
                AllowAllOutbound = true,
                Role = new Role(this, "WebServerInstanceRole", new RoleProps
                {
                    AssumedBy = new ServicePrincipal("ec2.amazonaws.com"),
                    ManagedPolicies = new IManagedPolicy[]
                    {
                        ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSCodeDeployRole"),
                        ManagedPolicy.FromAwsManagedPolicyName("AmazonS3ReadOnlyAccess")
                    },
                }),
                Signals = Signals.WaitForCount(1, new SignalsOptions
                {
                    Timeout = Duration.Minutes(10)
                })
            });

            // Initialize the vanilla EC2 instances with our needed software at launch time using a UserData script
            scalingGroup.AddUserData(new string[]
            {
                // install and configure IIS with ASP.NET 4.6
                "Install-WindowsFeature -Name Web-Server,NET-Framework-45-ASPNET,NET-Framework-45-Core,NET-Framework-45-Features",
                // install WebDeploy, which I've found to be easiest using chocolatey
                "Set-ExecutionPolicy Bypass -Scope Process -Force; iex ((New-Object System.Net.WebClient).DownloadString('https://chocolatey.org/install.ps1'))",
                "choco install webdeploy -y",
                // download and install the CodeDeploy agent; note that the AddS3DownloadCommand method on UserData does
                // exactly what's below with Read-S3Object
                $"Read-S3Object -BucketName aws-codedeploy-{this.Region} -Key latest/codedeploy-agent.msi -File c:/temp/codedeploy-agent.msi",
                "c:/temp/codedeploy-agent.msi /quiet /l c:/temp/host-agent-install-log.txt",
            });

            scalingGroup.UserData.AddSignalOnExitCommand(scalingGroup);

            // Configure an application load balancer, listening on port 80, to front our fleet
            var loadBalancer = new ApplicationLoadBalancer(this, "WindowsWebServerFleetALB", new ApplicationLoadBalancerProps
            {
                Vpc = vpc,
                InternetFacing = true
            });

            var lbListener = loadBalancer.AddListener("Port80Listener", new BaseApplicationListenerProps
            {
                Port = 80
            });

            lbListener.AddTargets("Port80ListenerTargets", new AddApplicationTargetsProps
            {
                Port = 80,
                Targets = new [] { scalingGroup }
            });

            lbListener.Connections.AllowDefaultPortFromAnyIpv4("Open access to port 80");

            // Can only be set after the group has been attached to a load balancer
            scalingGroup.ScaleOnRequestCount("DemoLoad", new RequestCountScalingProps
            {
                TargetRequestsPerMinute = 10 // enough for demo purposes
            });

            #endregion

            #region Deployment resources

            var deploymentBucket = new Bucket(this, "AppDeploymentBucket", new BucketProps
            {
                Versioned = true
            });

            // Application and deployment group names are defined in cdk.json; we could choose to override
            // at the command line, for example -
            // cdk deploy -c DeploymentApplicationName=abc -c DeploymentGroupName=xyz).
            var applicationName = (string)this.Node.TryGetContext(DeploymentApplicationNameContextKey);
            var deploymentGroupName = (string)this.Node.TryGetContext(DeploymentGroupNameContextKey);

            var codeDeployApp = new ServerApplication(this, "WindowsWebServerFleetDeploymentApplication", new ServerApplicationProps
            {
                ApplicationName = applicationName
            });

            var deploymentGroup = new ServerDeploymentGroup(this, "WindowsWebServerFleetDeploymentGroup", new ServerDeploymentGroupProps
            {
                Application = codeDeployApp,
                AutoScalingGroups = new AutoScalingGroup[] { scalingGroup },
                DeploymentGroupName = deploymentGroupName,
                InstallAgent = false, // we did this already as part of EC2 instance intitialization userdata
                Role = new Role(this, "WindowsWebServerFleetDeploymentRole", new RoleProps
                {
                    AssumedBy = new ServicePrincipal("codedeploy.amazonaws.com"),
                    ManagedPolicies = new IManagedPolicy[]
                    {
                        ManagedPolicy.FromAwsManagedPolicyName("service-role/AWSCodeDeployRole")
                    }
                }),
                DeploymentConfig = ServerDeploymentConfig.ONE_AT_A_TIME
            });

            #endregion

            #region Deployment pipeline resources

            var trail = new Trail(this, "WindowsWebServerTrail");
            trail.AddS3EventSelector(
                new []
                {
                    new S3EventSelector
                    {
                        Bucket = deploymentBucket
                    }
                },
                new AddEventSelectorOptions
                {
                    ReadWriteType = ReadWriteType.WRITE_ONLY
                }
            );

            var sourceOutput = new Artifact_("SourceBundle");
            var appBundleName = (string)this.Node.TryGetContext(AppBundleNameContextKey);

            var pipeline = new Pipeline(this, "WindowsWebServerPipeline", new PipelineProps
            {
                Stages = new []
                {
                    new Amazon.CDK.AWS.CodePipeline.StageProps
                    {
                        StageName = "BundleUpload",
                        Actions = new []
                        {
                            new S3SourceAction(new S3SourceActionProps
                            {
                                ActionName = "BundleUpload",
                                RunOrder = 1,
                                Bucket = deploymentBucket,
                                BucketKey = appBundleName,
                                Trigger = S3Trigger.EVENTS,
                                Output = sourceOutput
                            })
                        }
                    },
                    new Amazon.CDK.AWS.CodePipeline.StageProps
                    {
                        StageName = "BundleDeployment",
                        Actions = new []
                        {
                            new CodeDeployServerDeployAction(new CodeDeployServerDeployActionProps
                            {
                                ActionName = "DeployViaCodeDeploy",
                                RunOrder = 2,
                                DeploymentGroup = deploymentGroup,
                                Input = sourceOutput
                            })
                        }
                    }
                }
            });

            #endregion

            // Emit the name of the bucket, the key path (folder), and the filename to which app
            // bundles ready for deployment should be uploaded
            new CfnOutput(this, "AppDeploymentBucketName", new CfnOutputProps
            {
                Value = deploymentBucket.BucketName
            });
            new CfnOutput(this, "AppDeploymentBucketUploadKeyPath", new CfnOutputProps
            {
                Value = applicationName
            });
            new CfnOutput(this, "AppDeploymentBundleName", new CfnOutputProps
            {
                Value = appBundleName
            });

            // Emit the url to the load balancer fronting the application fleet
            new CfnOutput(this, "WindowsWebServerFleetUrl", new CfnOutputProps
            {
                Value = loadBalancer.LoadBalancerDnsName
            });
        }
    }
}
