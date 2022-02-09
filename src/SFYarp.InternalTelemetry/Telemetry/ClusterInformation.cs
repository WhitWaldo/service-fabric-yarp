﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Fabric;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Win32;

namespace Yarp.ServiceFabric.InternalTelemetry
{
    /// <summary>
    /// Helper class to facilitate non-PII identification of cluster.
    /// </summary>
    public sealed class ClusterInformation
    {
        private const string FabricRegistryKeyPath = "Software\\Microsoft\\Service Fabric";
        private static readonly object LockObj = new object();
        private static string paasClusterId;
        private static string diagnosticsClusterId;
        private static XmlDocument clusterManifestXdoc;

        /// <summary>
        /// Gets ClusterID, tenantID and ClusterType for current ServiceFabric cluster
        /// The logic to compute these values closely resembles the logic used in SF runtime's telemetry client.
        /// </summary>
        public static async Task<(string ClusterId, string TenantId, string ClusterType)> TupleGetClusterIdAndTypeAsync(FabricClient fabricClient, CancellationToken token)
        {
            string clusterManifest = await fabricClient.ClusterManager.GetClusterManifestAsync(
                                        TimeSpan.FromSeconds(TelemetryConstants.AsyncOperationTimeoutSeconds),
                                        token);

            // Get tenantId for PaasV1 clusters or SFRP.
            string tenantId = GetTenantId() ?? TelemetryConstants.Undefined;
            string clusterId = TelemetryConstants.Undefined;
            string clusterType = TelemetryConstants.Undefined;

            if (!string.IsNullOrEmpty(clusterManifest))
            {
                // Safe XML pattern - *Do not use LoadXml*.
                clusterManifestXdoc = new XmlDocument { XmlResolver = null };

                using (var sreader = new StringReader(clusterManifest))
                {
                    using (var xreader = XmlReader.Create(sreader, new XmlReaderSettings { XmlResolver = null }))
                    {
                        lock (LockObj)
                        {
                            clusterManifestXdoc?.Load(xreader);

                            // Get values from cluster manifest, clusterId if it exists in either Paas or Diagnostics section.
                            GetValuesFromClusterManifest();
                        }

                        if (paasClusterId != null)
                        {
                            clusterId = paasClusterId;
                            clusterType = TelemetryConstants.ClusterTypeSfrp;
                        }
                        else if (tenantId != TelemetryConstants.Undefined)
                        {
                            clusterId = tenantId;
                            clusterType = TelemetryConstants.ClusterTypePaasV1;
                        }
                        else if (diagnosticsClusterId != null)
                        {
                            clusterId = diagnosticsClusterId;
                            clusterType = TelemetryConstants.ClusterTypeStandalone;
                        }
                    }
                }
            }

            return (clusterId, tenantId, clusterType);
        }

        /// <summary>
        /// Gets the value of a parameter inside a section from the cluster manifest XmlDocument instance (clusterManifestXdoc).
        /// </summary>
        private static string GetParamValueFromSection(string sectionName, string parameterName)
        {
            if (clusterManifestXdoc == null)
            {
                return null;
            }

            XmlNode sectionNode = clusterManifestXdoc.DocumentElement?.SelectSingleNode("//*[local-name()='Section' and @Name='" + sectionName + "']");
            XmlNode parameterNode = sectionNode?.SelectSingleNode("//*[local-name()='Parameter' and @Name='" + parameterName + "']");
            XmlAttribute attr = parameterNode?.Attributes?["Value"];

            return attr?.Value;
        }

        private static string GetClusterIdFromPaasSection()
        {
            return GetParamValueFromSection("Paas", "ClusterId");
        }

        private static string GetClusterIdFromDiagnosticsSection()
        {
            return GetParamValueFromSection("Diagnostics", "ClusterId");
        }

        private static string GetTenantId()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetTenantIdWindows();
            }

            return GetTenantIdLinux();
        }

        private static string GetTenantIdLinux()
        {
            // Implementation copied from https://github.com/microsoft/service-fabric/blob/master/src/prod/src/managed/DCA/product/host/TelemetryConsumerLinux.cs
            const string TenantIdFile = "/var/lib/waagent/HostingEnvironmentConfig.xml";

            if (!File.Exists(TenantIdFile))
            {
                return null;
            }

            string tenantId;
            var xmlDoc = new XmlDocument { XmlResolver = null };

            using (var xmlReader = XmlReader.Create(TenantIdFile, new XmlReaderSettings { XmlResolver = null }))
            {
                xmlDoc.Load(xmlReader);
            }

            tenantId = xmlDoc.GetElementsByTagName("Deployment").Item(0).Attributes.GetNamedItem("name").Value;
            return tenantId;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GetTenantIdWindows()
        {
            const string TenantIdValueName = "WATenantID";
            string tenantIdKeyName = string.Format(CultureInfo.InvariantCulture, "{0}\\{1}", Registry.LocalMachine.Name, FabricRegistryKeyPath);

            return (string)Registry.GetValue(tenantIdKeyName, TenantIdValueName, null);
        }

        private static void GetValuesFromClusterManifest()
        {
            paasClusterId = GetClusterIdFromPaasSection();
            diagnosticsClusterId = GetClusterIdFromDiagnosticsSection();
        }
    }
}
