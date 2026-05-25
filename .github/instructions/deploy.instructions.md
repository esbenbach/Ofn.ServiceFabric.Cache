---
applyTo: "Deploy/**"
---

# Deployment scripts conventions

- PowerShell scripts for Service Fabric application deployment.
- `SFApp.Common.psm1` is a shared module imported by other scripts.
- `Deploy-SFApp.ps1` deploys the application package to the cluster.
- `Package-SFApp.ps1` packages the SF application for deployment.
- `Remove-SFApp.ps1` removes the application from the cluster.
- `azure-pipelines.yml` defines the CI/CD pipeline.
- These scripts are Windows PowerShell (not cross-platform PowerShell Core). They depend on the Service Fabric SDK being installed.
