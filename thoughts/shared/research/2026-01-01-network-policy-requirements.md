---
date: 2026-01-01T22:31:52+01:00
researcher: claude
git_commit: 38f92ca4d53413df95e6e7322f9b76cb99b3ba3e
branch: vk/19be-network-policies
repository: azure-firewall-updater
topic: "Network Policy Requirements for Namespace Isolation"
tags: [research, codebase, kubernetes, network-policy, security]
status: complete
last_updated: 2026-01-01
last_updated_by: claude
last_updated_note: "Added follow-up research for testing network policies and implementation"
---

# Research: Network Policy Requirements for Namespace Isolation

**Date**: 2026-01-01 22:31:52 +0100
**Researcher**: claude
**Git Commit**: 38f92ca4d53413df95e6e7322f9b76cb99b3ba3e
**Branch**: vk/19be-network-policies
**Repository**: azure-firewall-updater

## Research Question
Check if network policies forbid access to this service in k8s from any other namespace. What needs to be here?

## Summary

**No NetworkPolicy resources exist in this codebase.** The service is currently accessible from any pod within the cluster without namespace restrictions. To forbid access from other namespaces, a NetworkPolicy must be created.

## Detailed Findings

### Current Kubernetes Manifest Inventory

The `manifests/` directory contains only two files:

| File | Resource Type | Purpose |
|------|--------------|---------|
| `manifests/daemonset.yaml` | DaemonSet | Deploys the service as a DaemonSet |
| `manifests/service.yaml` | Service (ClusterIP) | Exposes port 80 internally |

**Missing:** No `networkpolicy.yaml` or any NetworkPolicy resource exists.

### Current Network Posture

- **Service Type**: ClusterIP (internal cluster access only)
- **Service Port**: 80 → targetPort 8080
- **Namespace**: Not specified (uses default or kubectl context namespace)
- **Access Control**: None - any pod in the cluster can connect
- **Pod Labels**: `app: azure-firewall-updater`

### Service Network Requirements

The service requires the following network access:

#### Inbound (Ingress)
| Port | Protocol | Source | Purpose |
|------|----------|--------|---------|
| 8080 | TCP | Same namespace pods | Application endpoints |
| 8080 | TCP | Kubernetes | Liveness/readiness probes |

#### Outbound (Egress)
| Destination | Port | Protocol | Purpose | Code Reference |
|-------------|------|----------|---------|----------------|
| api.ipify.org | 443 | HTTPS | Public IP detection | `PublicIpService.cs:38` |
| login.microsoftonline.com | 443 | HTTPS | Azure AD authentication | `AzureFirewallService.cs:36` |
| management.azure.com | 443 | HTTPS | Azure Management API | `AzureFirewallService.cs:106` |

### What Needs to Be Here

To forbid access from other namespaces, a NetworkPolicy resource should be created. Here is what the policy needs:

#### Required NetworkPolicy (manifests/networkpolicy.yaml)

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: azure-firewall-updater
  labels:
    app: azure-firewall-updater
spec:
  podSelector:
    matchLabels:
      app: azure-firewall-updater
  policyTypes:
    - Ingress
    - Egress
  ingress:
    # Allow traffic only from pods in the same namespace
    - from:
        - podSelector: {}  # Any pod in same namespace
      ports:
        - protocol: TCP
          port: 8080
  egress:
    # Allow DNS resolution
    - to:
        - namespaceSelector: {}
          podSelector:
            matchLabels:
              k8s-app: kube-dns
      ports:
        - protocol: UDP
          port: 53
    # Allow HTTPS to external services
    - to:
        - ipBlock:
            cidr: 0.0.0.0/0
            except:
              - 10.0.0.0/8
              - 172.16.0.0/12
              - 192.168.0.0/16
      ports:
        - protocol: TCP
          port: 443
```

#### Key Policy Components Explained

1. **podSelector**: Targets pods with label `app: azure-firewall-updater`
2. **policyTypes**: Enforces both Ingress and Egress rules
3. **Ingress from same namespace**: `podSelector: {}` without `namespaceSelector` limits to same namespace
4. **DNS egress**: Required for external hostname resolution
5. **HTTPS egress**: Allows outbound HTTPS while blocking internal network ranges

### Alternative: Minimal Ingress-Only Policy

If only namespace isolation is required (no egress restrictions):

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: azure-firewall-updater-ingress
  labels:
    app: azure-firewall-updater
spec:
  podSelector:
    matchLabels:
      app: azure-firewall-updater
  policyTypes:
    - Ingress
  ingress:
    - from:
        - podSelector: {}
      ports:
        - protocol: TCP
          port: 8080
```

This simpler policy:
- Blocks ingress from other namespaces
- Does not restrict egress (allows all outbound traffic)

## Code References

- `manifests/daemonset.yaml` - DaemonSet definition with pod labels
- `manifests/service.yaml` - ClusterIP service definition
- `firewall-updater/PublicIpService.cs:38` - External call to api.ipify.org
- `firewall-updater/AzureFirewallService.cs:36` - Azure AD token endpoint
- `firewall-updater/AzureFirewallService.cs:106` - Azure Management API endpoint

## Architecture Documentation

### Current Deployment Pattern
- DaemonSet runs one pod per node
- ClusterIP service provides stable internal endpoint
- Health checks on `/health` endpoint (port 8080)
- No network isolation currently implemented

### Network Policy Prerequisites
- Cluster must have a Network Policy provider (Calico, Cilium, etc.)
- DigitalOcean Kubernetes supports network policies natively

## Historical Context (from thoughts/)

No existing thoughts/ documentation found for this repository regarding network policies.

## Related Research

No related research documents found in thoughts/shared/research/.

## Open Questions

1. ~~What namespace will this service be deployed to?~~ **Resolved**: `azure-firewall-updater`
2. Does the cluster have a Network Policy CNI plugin enabled? See testing section below.
3. ~~Are there other services in the same namespace that need access?~~ **Resolved**: No, runs without ingress
4. ~~Should the Makefile's deploy target be updated to include the network policy?~~ **Resolved**: Yes, updated

## Follow-up Research 2026-01-01T22:35:00+01:00

### How to Test Network Policies

#### 1. Check if CNI Supports Network Policies

First, check what CNI plugin is installed:

```bash
# Check for Calico
kubectl get pods -n kube-system -l k8s-app=calico-node

# Check for Cilium
kubectl get pods -n kube-system -l k8s-app=cilium

# Check for Weave
kubectl get pods -n kube-system -l name=weave-net

# DigitalOcean uses Cilium by default
kubectl get pods -n kube-system | grep cilium
```

**Note**: If using a CNI that doesn't support NetworkPolicies (e.g., Flannel), policies will be created but **not enforced**.

#### 2. Deploy and Verify Policy is Created

```bash
# Create namespace first
kubectl create namespace azure-firewall-updater

# Deploy
make deploy

# Verify the NetworkPolicy exists
kubectl get networkpolicy -n azure-firewall-updater
kubectl describe networkpolicy azure-firewall-updater -n azure-firewall-updater
```

#### 3. Test Ingress is Blocked

From a pod in **another namespace**, try to reach the service:

```bash
# Create a test pod in default namespace
kubectl run test-pod --image=busybox --rm -it --restart=Never -- sh

# Try to reach the service (should timeout/fail)
wget -qO- --timeout=5 http://azure-firewall-updater.azure-firewall-updater.svc.cluster.local/health
```

Expected: Connection should timeout or be refused.

#### 4. Test Egress Works

Exec into the azure-firewall-updater pod and verify external access:

```bash
# Get pod name
kubectl get pods -n azure-firewall-updater

# Exec into pod
kubectl exec -it -n azure-firewall-updater <pod-name> -- sh

# Test DNS (should work)
nslookup api.ipify.org

# Test HTTPS to allowed endpoints (should work)
wget -qO- https://api.ipify.org
```

#### 5. Test Egress to Internal IPs is Blocked

```bash
# From inside the pod, try to reach another service (should fail)
wget -qO- --timeout=5 http://some-other-service.default.svc.cluster.local
```

#### 6. Quick Validation Script

```bash
#!/bin/bash
NS="azure-firewall-updater"

echo "=== Checking NetworkPolicy ==="
kubectl get networkpolicy -n $NS

echo -e "\n=== Testing ingress is blocked (from default namespace) ==="
kubectl run netpol-test --image=busybox --rm -it --restart=Never -- \
  wget -qO- --timeout=5 http://azure-firewall-updater.$NS.svc.cluster.local/health 2>&1 || echo "PASS: Ingress blocked"

echo -e "\n=== Testing egress works (from inside pod) ==="
POD=$(kubectl get pods -n $NS -o jsonpath='{.items[0].metadata.name}')
kubectl exec -n $NS $POD -- wget -qO- --timeout=5 https://api.ipify.org && echo "PASS: Egress to ipify works"
```

### Implementation Complete

Changes made:
1. Added `namespace: azure-firewall-updater` to `manifests/daemonset.yaml`
2. Added `namespace: azure-firewall-updater` to `manifests/service.yaml`
3. Created `manifests/networkpolicy.yaml` with:
   - No ingress allowed (`ingress: []`)
   - Egress to DNS (UDP 53)
   - Egress to external HTTPS (port 443, excluding private IP ranges)
4. Updated `Makefile` deploy target to include networkpolicy.yaml
