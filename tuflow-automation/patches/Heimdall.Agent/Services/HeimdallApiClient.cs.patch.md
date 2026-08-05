# Patch: Heimdall.Agent/Services/HeimdallApiClient.cs

Add one method, mirroring `GetResourceSamplingStatusAsync` immediately above
it — same shape (try/catch, `ApplyKey()`, `GetFromJsonAsync`, log-and-return-
null on failure so the caller just keeps polling rather than crashing):

```diff
     public async Task<ResourceSamplingStatusDto?> GetResourceSamplingStatusAsync(string hostname, CancellationToken ct)
     {
         try
         {
             ApplyKey();
             return await http.GetFromJsonAsync<ResourceSamplingStatusDto>(
                 $"/api/resource-sampling/{Uri.EscapeDataString(hostname)}/status", ct);
         }
         catch (Exception ex)
         {
             logger.LogDebug(ex, "Resource sampling status poll failed");
             return null;
         }
     }
+
+    /// <summary>
+    /// Fast, independent poll (not tied to ConfigRefreshSeconds) for a queued TUFLOW start/stop — same
+    /// pattern as GetResourceSamplingStatusAsync above. Null on failure — caller just tries again next
+    /// tick rather than treating a transient network blip as "nothing pending".
+    /// </summary>
+    public async Task<TuflowPendingDto?> GetTuflowPendingAsync(string hostname, CancellationToken ct)
+    {
+        try
+        {
+            ApplyKey();
+            return await http.GetFromJsonAsync<TuflowPendingDto>(
+                $"/api/tuflow/{Uri.EscapeDataString(hostname)}/pending", ct);
+        }
+        catch (Exception ex)
+        {
+            logger.LogDebug(ex, "TUFLOW pending poll failed");
+            return null;
+        }
+    }
```

No other changes to this file — `using Heimdall.Shared.Contracts;` at the
top already covers `TuflowPendingDto`.
