using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class OvitJson
{
    internal static Dictionary<string, object> Map(params object[] pairs)
    {
        Dictionary<string, object> result = new Dictionary<string, object>();
        for (int i = 0; i < pairs.Length; i += 2) result.Add((string)pairs[i], pairs[i + 1]);
        return result;
    }
    internal static Dictionary<string, object> Object(object value)
    { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
    internal static object Get(Dictionary<string, object> map, string key, object fallback)
    { object value; return map.TryGetValue(key, out value) ? value : fallback; }
    internal static double Number(Dictionary<string, object> map, string key)
    {
        object value;
        if (!map.TryGetValue(key, out value)) throw new IOException("奥维响应缺少字段：" + key);
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!CoverageMath.Finite(number)) throw new IOException("奥维返回了无效数值：" + key);
        return number;
    }
    internal static string Serialize(object value)
    { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Serialize(value); }
    internal static Dictionary<string, object> Deserialize(string value)
    { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 64 }.Deserialize<Dictionary<string, object>>(value); }
}

internal sealed class OvitClient : IDisposable
{
    private readonly ClientWebSocket socket = new ClientWebSocket();
    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    private readonly SemaphoreSlim requestGate = new SemaphoreSlim(1, 1);
    private readonly object sync = new object();
    private TaskCompletionSource<Dictionary<string, object>> pending;
    private int pendingCommand;
    private bool disposed;
    internal bool IsConnected { get { return !disposed && socket.State == WebSocketState.Open; } }
    internal event Action<string> Disconnected;

    internal async Task ConnectAsync(string address, string password)
    {
        Uri uri;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out uri) ||
            (uri.Scheme != "ws" && uri.Scheme != "wss") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("请输入奥维 WebSocket 地址，例如 ws://127.0.0.1:7699。");
        socket.Options.AddSubProtocol("ws");
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            timeout.CancelAfter(8000);
            await socket.ConnectAsync(uri, timeout.Token);
        }
        // Receive starts before registration so even immediate replies are observed.
        Task receiver = ReceiveLoop();
        GC.KeepAlive(receiver);
        await RequestAsync(20000, OvitJson.Map("md5psk", PasswordDigest(password)), 8000);
    }

    internal static string PasswordDigest(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "";
        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(password));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
        }
    }

    internal async Task<Dictionary<string, object>> RequestAsync(int command, object message, int milliseconds)
    {
        await requestGate.WaitAsync(lifetime.Token);
        try
        {
            if (!IsConnected) throw new IOException("奥维连接已断开。");
            TaskCompletionSource<Dictionary<string, object>> completion =
                new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync) { pending = completion; pendingCommand = command; }
            byte[] bytes = Encoding.UTF8.GetBytes(OvitJson.Serialize(OvitJson.Map("cmdid", command, "ret", 0, "msg", message)));
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                timeout.CancelAfter(milliseconds);
                using (timeout.Token.Register(delegate { completion.TrySetCanceled(); }))
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
                    Dictionary<string, object> response;
                    try { response = await completion.Task; }
                    catch (TaskCanceledException)
                    {
                        // The protocol has no request id: retire this connection so a late
                        // pick reply cannot satisfy a subsequent pick on the same command.
                        Dispose();
                        throw new IOException("奥维操作超时或已取消，请重新连接。取点操作请在 120 秒内完成。");
                    }
                    int result = Convert.ToInt32(OvitJson.Get(response, "ret", 0), CultureInfo.InvariantCulture);
                    Dictionary<string, object> body = OvitJson.Object(OvitJson.Get(response, "msg", null));
                    string error = Convert.ToString(OvitJson.Get(body, "errmsg", ""), CultureInfo.InvariantCulture);
                    if (result < 0 || error.Length > 0)
                        throw new IOException("奥维返回 " + result + "：" + ErrorText(result, error));
                    return body;
                }
            }
        }
        finally
        {
            lock (sync) { pending = null; pendingCommand = 0; }
            requestGate.Release();
        }
    }

    private static string ErrorText(int code, string error)
    {
        if (code == -5) return "当前奥维账户未开通该接口所需的会员权限。";
        if (code == -4) return "请求参数或图形格式不被当前奥维版本接受。";
        return error.Length > 0 ? error : "请检查奥维接口设置及版本。";
    }

    private async Task ReceiveLoop()
    {
        try
        {
            byte[] buffer = new byte[16384];
            while (IsConnected)
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close) throw new IOException("奥维已关闭连接。");
                        if (received.MessageType != WebSocketMessageType.Text) throw new IOException("奥维返回了非文本协议数据。");
                        stream.Write(buffer, 0, received.Count);
                        if (stream.Length > 8 * 1024 * 1024) throw new IOException("奥维响应超过 8 MB。");
                    } while (!received.EndOfMessage);
                    Dictionary<string, object> response = OvitJson.Deserialize(Encoding.UTF8.GetString(stream.ToArray()));
                    int command = Convert.ToInt32(OvitJson.Get(response, "cmdid", 0), CultureInfo.InvariantCulture);
                    if (command == 20008) throw new IOException("奥维已退出。");
                    lock (sync)
                    {
                        if (pending != null && command == pendingCommand) pending.TrySetResult(response);
                    }
                }
            }
        }
        catch (Exception error)
        {
            bool notify = !disposed;
            lock (sync) { if (pending != null) pending.TrySetException(new IOException(error.Message, error)); }
            Dispose();
            Action<string> handler = Disconnected;
            if (notify && handler != null) handler(error.Message);
        }
    }

    internal async Task<GeoPoint> PickAsync()
    {
        Dictionary<string, object> response = await RequestAsync(20001, OvitJson.Map(), 120000);
        return CoverageCoordinates.FromOvit(OvitJson.Number(response, "lat"), OvitJson.Number(response, "lng"),
            (int)OvitJson.Number(response, "CoordType"));
    }

    internal async Task LocateAsync(GeoPoint point)
    {
        Dictionary<string, object> info = await RequestAsync(20012, OvitJson.Map(), 8000);
        int map = Convert.ToInt32(OvitJson.Get(info, "MapType", 0), CultureInfo.InvariantCulture);
        int level = Convert.ToInt32(OvitJson.Get(info, "MapLevel", 16), CultureInfo.InvariantCulture);
        // Keep the current basemap and zoom. CoordType here uses 0=real, unlike LatlngType.
        await RequestAsync(20011, OvitJson.Map("Location", OvitJson.Map("MapID", map, "MapLevel", level,
            "lat", point.Latitude, "lng", point.Longitude, "CoordType", 0, "Active", 1)), 8000);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); socket.Abort(); socket.Dispose();
        lock (sync) { if (pending != null) pending.TrySetCanceled(); }
    }
}

internal sealed class OvitObject
{
    internal string Key;
    internal int Type;
    internal Dictionary<string, object> Data;
}

internal static class OvitDrawing
{
    internal const int TemporaryParent = 202;
    internal const int Red = 255, Blue = 16711680; // Windows COLORREF: 0x00BBGGRR.

    internal static List<OvitObject> Objects(CoverageParameters p, CoverageGeometry geometry)
    {
        List<OvitObject> objects = new List<OvitObject>();
        if (geometry.Fill.Count >= 3)
        {
            Dictionary<string, object> fill = BaseDetail();
            fill["Mtp"] = geometry.Fill.Count; fill["Latlng"] = Flatten(geometry.Fill);
            fill["LineClr"] = Red; fill["LineWidth"] = (int)p.LineWidth;
            fill["LineAlpha"] = 100; fill["AreaClr"] = Blue; fill["AreaAlpha"] = (int)Math.Round(p.Transparency);
            fill["StartAngle"] = 0; fill["EndAngle"] = 0; fill["OuterRgnIdx"] = 0; fill["ShowFlag"] = 0;
            fill["FillType"] = 0; fill["FillSubType"] = 0; fill["FillPattern"] = 0;
            fill["FillChgClr"] = 16777215; fill["FillRotate"] = 0; fill["FillScale"] = 0;
            fill["FillOffsetX"] = 0; fill["FillOffsetY"] = 0;
            objects.Add(Wrap("fill", "天线覆盖", 13, fill));
        }
        for (int i = 0; i < geometry.Lines.Count; i++)
        {
            CoveragePath path = geometry.Lines[i];
            Dictionary<string, object> detail = BaseDetail();
            detail["Mtp"] = path.Points.Count; detail["Latlng"] = Flatten(path.Points);
            detail["AltitudeColor"] = 0; detail["ShowName"] = 0; detail["Edit"] = 0;
            detail["TrackDraw"] = OvitJson.Map("LineClr", Red, "LineWidth", (int)p.LineWidth, "LineAlpha", 0,
                "ShowType", 0, "LineType", path.Dashed ? 1 : 0, "CircleClr", Red, "CircleWidth", 0,
                "NeedDrawDetail", 0, "Close", 0);
            objects.Add(Wrap("line:" + PathKey(path.Name), path.Name, 8, detail));
        }
        foreach (CoverageMarker marker in geometry.Markers)
        {
            Dictionary<string, object> detail = BaseDetail();
            detail["Lat"] = marker.Point.Latitude; detail["Lng"] = marker.Point.Longitude;
            detail["Altitude"] = 0; detail["TxtType"] = 3; detail["TimeUncertain"] = 0;
            detail["TxtShowSta"] = 0; detail["TxtShowStaSet"] = 0;
            detail["SignPic"] = OvitJson.Map("PicClr", Red, "Style", 0, "PicScale", 0,
                "SignPicNum", 0, "SignPicNumOffx", 0, "SignPicNumOffy", 0, "SignPicNumClr", Red, "SignPicNumSize", 0);
            detail["SignEvent"] = OvitJson.Map("GroupTime", 0, "Type", 0, "TypeExt", 0);
            objects.Add(Wrap("marker:" + PathKey(marker.Name), marker.Name, 7, detail));
        }
        return objects;
    }

    private static string PathKey(string name) { return name.Split(' ')[0]; }
    private static Dictionary<string, object> BaseDetail()
    {
        return OvitJson.Map("Gcj02", 0, "OverlayIdx", 0, "TrackType", 0, "ShowLevel", 1,
            "ShowLevelMax", 0, "ShowLevelBak", 0, "EditMode", 0);
    }
    private static OvitObject Wrap(string key, string name, int type, Dictionary<string, object> detail)
    {
        return new OvitObject { Key = key, Type = type, Data = OvitJson.Map("Type", type, "ObjID", "0",
            "ParentID", TemporaryParent, "SrvID", "0", "Object", OvitJson.Map("Name", name, "Comment",
            "Compass 天线覆盖：平坦地面波瓣几何投影", "Type", type, "ObjectDetail", detail)) };
    }
    private static double[] Flatten(List<GeoPoint> points)
    {
        double[] result = new double[points.Count * 2];
        for (int i = 0; i < points.Count; i++) { result[2 * i] = points[i].Latitude; result[2 * i + 1] = points[i].Longitude; }
        return result;
    }

    internal static Dictionary<string, object> Tree(OvitObject item, string id)
    {
        item.Data["ObjID"] = id;
        Dictionary<string, object> detail = OvitJson.Map("Child", 1, "AllChild", 1, "LoadOk", 1,
            "SaveMerge", 0, "Group", 0, "AutoLoad", 1, "ShowLevel", 1, "ShowLevelMax", 0,
            "ShowLevelBak", 0, "Crypt", 0, "Share", 0, "ReadOnly", 0, "NotHotId", 0,
            "Bind", 0, "BindCheck", 0, "Link", 0, "LinkAutoCheck", 0, "ChildiOverlay", 0,
            "LinkUrl", "", "Relate", 0, "ObjChildren", new object[] { item.Data });
        return OvitJson.Map("ObjTree", OvitJson.Map("idxTree", 1, "Type", 30, "ObjID", "0", "ParentID", TemporaryParent,
            "tmModify", DateTime.Now.ToString("yyyy/MM/dd?HH:mm:ss", CultureInfo.InvariantCulture),
            "Object", OvitJson.Map("Name", "", "Type", 30, "ObjectDetail", detail)));
    }
}

internal sealed class OvitDrawingSession
{
    private readonly OvitClient client;
    private readonly Dictionary<string, string> ids = new Dictionary<string, string>();
    internal OvitDrawingSession(OvitClient client) { this.client = client; }

    internal async Task UpdateAsync(CoverageParameters p, CoverageGeometry geometry)
    {
        List<OvitObject> objects = OvitDrawing.Objects(p, geometry);
        HashSet<string> active = new HashSet<string>();
        foreach (OvitObject item in objects)
        {
            active.Add(item.Key);
            string id; if (!ids.TryGetValue(item.Key, out id)) id = "0";
            // One object per request makes the returned id unambiguous, including
            // partial failures. Only ids acknowledged by this session are managed.
            Dictionary<string, object> reply = await client.RequestAsync(20112, OvitDrawing.Tree(item, id), 8000);
            IEnumerable items = OvitJson.Get(reply, "ObjItems", null) as IEnumerable;
            string acknowledged = null;
            if (items != null) foreach (object raw in items)
            {
                Dictionary<string, object> obj = OvitJson.Object(raw);
                if (Convert.ToInt32(OvitJson.Get(obj, "Type", 0), CultureInfo.InvariantCulture) == item.Type)
                    acknowledged = Convert.ToString(OvitJson.Get(obj, "ObjID", ""), CultureInfo.InvariantCulture);
            }
            long numeric;
            if (!long.TryParse(acknowledged, out numeric) || numeric <= 0 || numeric == OvitDrawing.TemporaryParent)
                throw new IOException("奥维未返回有效图形 ID，请核对会员权限及接口版本。");
            ids[item.Key] = acknowledged;
        }
        List<string> stale = new List<string>();
        foreach (string key in ids.Keys) if (!active.Contains(key)) stale.Add(key);
        await DeleteAsync(stale);
    }

    internal Task ClearAsync() { return DeleteAsync(new List<string>(ids.Keys)); }

    private async Task DeleteAsync(List<string> keys)
    {
        if (keys.Count == 0) return;
        List<long> list = new List<long>();
        foreach (string key in keys) list.Add(long.Parse(ids[key], CultureInfo.InvariantCulture));
        await client.RequestAsync(20103, OvitJson.Map("NoGroup", 1, "ListID", list), 8000);
        foreach (string key in keys) ids.Remove(key);
    }
}
