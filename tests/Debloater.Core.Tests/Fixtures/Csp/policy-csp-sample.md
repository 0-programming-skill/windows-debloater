---
layout: Conceptual
title: Sample Policy CSP | Microsoft Learn
ms.date: 2026-09-10T00:00:00.0000000Z
updated_at: 2026-09-23T17:34:00.0000000Z
---

# Sample Policy CSP | Microsoft Learn

Intro text that is not a policy.

## AllowTelemetry

| Scope | Editions | Applicable OS |
| --- | --- | --- |
| ✅ Device  ✅ User | ✅ Pro  ✅ Enterprise  ✅ Education  ✅ IoT Enterprise / IoT Enterprise LTSC | ✅ Windows 10, version 1507 [10.0.10240] and later |

```User
./User/Vendor/MSFT/Policy/Config/Sample/AllowTelemetry
```

```Device
./Device/Vendor/MSFT/Policy/Config/Sample/AllowTelemetry
```

- Diagnostic data off. This value is only supported on Enterprise, Education, and Server editions.

**Description framework properties**:

| Property name | Property value |
| --- | --- |
| Format | `int` |
| Access Type | Add, Delete, Get, Replace |
| Default Value | 1 |

**Allowed values**:

| Value | Description |
| --- | --- |
| 0 | Security. Note: This value is only applicable to Windows 10 Enterprise and Windows 10 Education. |
| 1 (Default) | Basic. See [the docs](https://example.com/x). |
| 3 | Full. |

**Group policy mapping**:

| Name | Value |
| --- | --- |
| Name | AllowTelemetry |
| Friendly Name | Allow Diagnostic Data |
| Location | Computer and User Configuration |
| Path | WindowsComponents &gt; Data Collection and Preview Builds |
| Registry Key Name | Software\Policies\Microsoft\Windows\DataCollection |
| ADMX File Name | DataCollection.admx |

## EnterpriseOnly

| Scope | Editions | Applicable OS |
| --- | --- | --- |
| ✅ Device  ❌ User | ❌ Pro  ✅ Enterprise  ✅ Education  ✅ IoT Enterprise / IoT Enterprise LTSC | ✅ Windows 11, version 22H2 with [KB5030310](https://support.microsoft.com/help/5030310) [10.0.22621.2361] and later  ✅ Windows 11, version 23H2 [10.0.22631] and later |

```Device
./Device/Vendor/MSFT/Policy/Config/Sample/EnterpriseOnly
```

This policy is deprecated and will only work on Windows 10 version 1809.

**Description framework properties**:

| Property name | Property value |
| --- | --- |
| Format | `int` |
| Default Value | 0 |
| Allowed Values | Range: `[0-365]` |

**Group policy mapping**:

| Name | Value |
| --- | --- |
| Name | Enterprise\_Only |
| Friendly Name | Enterprise only |
| Location | Computer Configuration |
| Path | Windows Components &gt; Sample |
| Registry Key Name | Software\Policies\Microsoft\Sample |
| Registry Value Name | Enterprise\_Only |
| ADMX File Name | Sample.admx |

## Windows Insider Preview

### PreviewThing

| Scope | Editions | Applicable OS |
| --- | --- | --- |
| ❌ Device  ✅ User | ✅ Pro  ✅ Enterprise  ✅ Education  ✅ IoT Enterprise / IoT Enterprise LTSC | ✅ Windows Insider Preview |

```User
./User/Vendor/MSFT/Policy/Config/Sample/PreviewThing
```

**Description framework properties**:

| Property name | Property value |
| --- | --- |
| Format | `chr` (string) |
| Access Type | Add, Delete, Get, Replace |

Tip

This is an ADMX-backed policy and requires SyncML format for configuration.

**ADMX mapping**:

| Name | Value |
| --- | --- |
| Name | PreviewThing |
| Friendly Name | Preview thing |
| Location | User Configuration |
| Path | Windows Components &gt; Sample |
| Registry Key Name | Software\Policies\Microsoft\Sample |
| ADMX File Name | Sample.admx |

## Related articles

[Policy configuration service provider](policy-configuration-service-provider.md)
