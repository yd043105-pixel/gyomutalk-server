using System.Text.Json;
using System.Windows;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    RemoteClient? remote;
    Toast? toast;
    public readonly HashSet<string> MutedRooms = [];
    readonly Dictionary<string, long> chatSeen = [];
    readonly HashSet<string> knownSurveys = [];
    public event Action<string>? ChatChanged;
    public event Action<string>? SurveyChanged;
    void ShowToast(string title, string text, string target, string? room = null)
    {
        if (DoNotDisturb.IsChecked) return;
        if (toast is null) { toast = new Toast(); toast.Closed += (_, _) => toast = null; }
        toast.Present(title, text, () => OpenNotification(target, room));
        if (!Quiet.IsChecked) System.Media.SystemSounds.Asterisk.Play();
    }
    void NotifyFeature(string text, string target) => ShowToast(text, "눌러서 확인하세요.", target);
    void OpenNotification(string target, string? room)
    {
        if (Api is null) return;
        if (target == "chat")
        {
            if (SessionWindows.OfType<ChatWindow>().FirstOrDefault() is not { } open) { new ChatWindow(this, people, null, room).Show(); return; }
            open.Activate(); if (room is not null) _ = Run(() => open.ShowRoom(room));
        }
        else if (target == "survey") OpenSurvey();
        else if (target == "timetable") { if (TimetableButton.IsVisible) new TimetableWindow(this, room).Show(); }
        else OpenMailbox("unread");
    }
    // Everything unread in one number: mail and chat together, without rooms set to silent or the room being read right now.
    async Task NotifyUnread(string target, string? room, ChatRoom[]? rooms = null)
    {
        if (Api is null) return;
        await UpdateUnread();
        rooms ??= await Archive.LocalRooms(await Api.Get<ChatRoom[]>("api/chats"));
        var chats = rooms.Where(r => !MutedRooms.Contains(r.Id) && !Reading(r.Id)).Sum(r => r.Unread);
        foreach (var known in rooms) chatSeen[known.Id] = known.LastAt;
        if (unreadCount + chats == 0) return;
        var goal = target == "message" && unreadCount > 0 || chats == 0 ? "message" : "chat";
        ShowToast($"안 읽은 메시지 {unreadCount + chats}{(unreadCount == 100 ? "+" : "")}개", $"업무 메시지 {unreadCount}{(unreadCount == 100 ? "+" : "")} · 채팅 {chats}", goal, goal == "chat" ? room ?? rooms.FirstOrDefault(r => r.Unread > 0 && !MutedRooms.Contains(r.Id))?.Id : null);
    }
    bool Reading(string room) => SessionWindows.OfType<ChatWindow>().Any(w => w.IsActive && w.RoomId == room);
    async Task NotifyChat(string id)
    {
        if (Api is null) return;
        var rooms = await Archive.LocalRooms(await Api.Get<ChatRoom[]>("api/chats"));
        // Someone leaving or a room being made changes nothing to read; only a newer message from someone else does.
        if (rooms.FirstOrDefault(r => r.Id == id) is not { Unread: > 0 } room || room.LastAt <= chatSeen.GetValueOrDefault(id)) { foreach (var known in rooms) chatSeen[known.Id] = known.LastAt; return; }
        if (MutedRooms.Contains(id) || Reading(id)) { chatSeen[id] = room.LastAt; return; }
        await NotifyUnread("chat", id, rooms);
    }
    // Several changed days arrive together when a weekly file goes up, so they share one notice.
    async Task NotifyTimetable()
    {
        var source=Api;
        if (source is null || !await RefreshTimetableAvailability()) return;
        SchoolMessenger.Contracts.TimetableNotice[] notices;
        try { notices=await source.Get<SchoolMessenger.Contracts.TimetableNotice[]>("api/timetable/notices"); }
        catch(ApiRequestException e) when(e.StatusCode==System.Net.HttpStatusCode.Forbidden) { await RefreshTimetableAvailability(); return; }
        if(source!=Api||TimetableButton.Visibility!=Visibility.Visible)return;
        var fresh=notices.Where(n => knownTimetables.Add(n.Revision) && n.ReadAt is null).OrderBy(n => n.Date, StringComparer.Ordinal).ToArray();
        foreach (var window in SessionWindows.OfType<TimetableWindow>().ToArray()) await window.Refresh();
        if (fresh.Length > 0) ShowToast(fresh.Length == 1 ? "시간표가 변경되었습니다" : $"시간표 변경 {fresh.Length}건", fresh[0].Title, "timetable", fresh[0].Date);
    }
    int timetableAvailabilityRequest;
    async Task<bool> RefreshTimetableAvailability()
    {
        var source=Api;if(source is null)return false;
        var request=++timetableAvailabilityRequest;
        var enabled=true; // Older school servers have no toggle and always show the timetable.
        try { enabled=(await source.Get<JsonElement>("api/timetable/status")).GetProperty("enabled").GetBoolean(); }
        catch(ApiRequestException e) when(e.StatusCode==System.Net.HttpStatusCode.NotFound) { }
        if(source!=Api||request!=timetableAvailabilityRequest)return false;
        TimetableButton.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;
        if(!enabled)foreach(var window in SessionWindows.OfType<TimetableWindow>().ToArray())window.Close();
        return enabled;
    }
    async Task NotifySurvey(string id)
    {
        if (Api is null) return;
        var survey = (await Api.Get<SurveyItem[]>("api/surveys")).FirstOrDefault(s => s.Id == id);
        if (survey is { Anonymous: true } && survey.OwnerId == Api.Session!.User!.Id) await Archive.KeepBallots(await Api.Get<SurveyResults>($"api/surveys/{id}/results"), id);
        if (survey is { Target: true, Answered: false, Closed: false } && survey.OwnerId != Api.Session!.User!.Id && knownSurveys.Add(id)) NotifyFeature("새 설문이 도착했습니다.", "survey");
    }
    void OpenChatClick(object sender, RoutedEventArgs e) => OpenChat();
    void OpenSurveyClick(object sender, RoutedEventArgs e) => OpenSurvey();
    void OpenWorkClick(object sender, RoutedEventArgs e) { if (Api is not null) new WorkWindow(this).Show(); }
    void OpenTimetableClick(object sender,RoutedEventArgs e){if(Api is not null&&TimetableButton.IsVisible)new TimetableWindow(this).Show();}
    void OpenChat(string[]? ids = null)
    {
        if (Api is null) return;
        var selected = ids ?? [];
        var valid = selected.Where(id => id != Api.Session!.User!.Id).ToArray();
        if (ids is not null && valid.Length == 0) { StatusLabel.Text = "다른 교직원을 선택하세요."; return; }
        new ChatWindow(this, people, valid.Length > 0 ? valid : null).Show();
    }
    void OpenSurvey(string[]? ids = null) { if (Api is not null) new SurveyWindow(this, people, ids).Show(); }
    Task RequestRemote(string person, bool shareMine)
    { remote ??= new RemoteClient(this); return remote.Request(person, shareMine); }
    async Task VerifyFeatureUi(string directory)
    {
        var other = people.First(p => p.Id != Api!.Session!.User!.Id);
        var work = new WorkWindow(this); work.Show(); await work.Verify(); SavePreview(work, Path.Combine(directory, "tasks-preview.png"));
        var collection = new ComposeWindow(this, people, other.Id, "학급별 체험학습 인솔 명단 제출"); collection.Show(); collection.BodyBox.Text = "학급별 인솔 명단을 제출해 주세요."; collection.CollectFiles.IsChecked = true; collection.SubmissionDate.SelectedDate = DateTime.Today.AddDays(7);
        collection.SendButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        JsonElement[] collectionRequests = [];
        for (var i = 0; i < 100; i++) { collectionRequests = await Api!.Get<JsonElement[]>("api/submission-requests"); if (collectionRequests.Any(r => r.GetProperty("title").GetString() == "학급별 체험학습 인솔 명단 제출")) break; await Task.Delay(50); }
        var collectionId = collectionRequests.FirstOrDefault(r => r.GetProperty("title").GetString() == "학급별 체험학습 인솔 명단 제출");
        if (collectionId.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Windows 쪽지 제출 요청 생성 실패");
        await work.Submission(Environment.GetEnvironmentVariable("YY_SUBMISSION_PREVIEW_ID")??collectionId.GetProperty("id").GetString()!); SavePreview(work, Path.Combine(directory, "submissions-preview.png")); work.Close();
        var chat = new ChatWindow(this, people); chat.Show();
        await chat.CreateRoom([other.Id], "1학년 담임 협의");
        for (var i = 0; i < 30 && chat.RoomId is null; i++) await Task.Delay(50);
        chat.BodyBox.Text = "내일 조회 때 체험학습 안내문을 같이 나눠 주세요."; await chat.SendMessage();
        if (!Archive.ChatEntries(chat.RoomId!).Any(m => m.Body.Contains("체험학습 안내문"))) throw new InvalidOperationException("채팅 UI 전송 실패");
        if ((await Api!.Get<ChatEntry[]>($"api/chats/{chat.RoomId}/messages")).Length != 0) throw new InvalidOperationException("이 PC가 저장한 채팅을 서버가 다시 내줌");
        SavePreview(chat, Path.Combine(directory, "chat-preview.png"));
        if (Archive.Search("이 문구는 어디에도 없습니다").Any()) throw new InvalidOperationException("메시지 검색 결과 오류");
        var askingFor = (await Api.Send<JsonElement>(HttpMethod.Post, "api/messages", new { clientId = Guid.NewGuid().ToString(), title = "검증", body = "다음 주 수요일까지 명단을 제출해 주세요.", recipientIds = new[] { Api.Session!.User!.Id }, attachmentIds = Array.Empty<string>(), all = false })).GetProperty("id").GetString()!;
        Archive.Save(await Api.Get<Detail>("api/messages/" + askingFor + "?peek=true"));
        if (Archive.Suggestions(askingFor).Length != 0) throw new InvalidOperationException("내가 보낸 메시지를 할 일 후보로 추천");
        var waiting = Path.Combine(directory, "waiting-file.txt"); File.WriteAllText(waiting, "synthetic");
        async Task Gone(string id) { try { await Api.Send<JsonElement>(HttpMethod.Delete, $"api/attachments/{id}"); } catch (ApiRequestException error) when ((int)error.StatusCode == 404) { return; } throw new InvalidOperationException("대기 목록에서 뺀 파일이 서버에 남음"); }
        await Gone(await chat.VerifyTakeBack(waiting));
        var writing = new ComposeWindow(this, people, other.Id, "파일 빼기 확인"); writing.Show(); await Gone(await writing.VerifyTakeBack(waiting)); File.Delete(waiting);
        var verifiedRoom = chat.RoomId!;
        await Api.Send<JsonElement>(HttpMethod.Post, $"api/chats/{verifiedRoom}/name", new { name = "내 이름표" }); await chat.ShowRoom(verifiedRoom);
        if (chat.RoomTitle.Text != "내 이름표") throw new InvalidOperationException("대화방 이름 바꾸기 표시 실패");
        await Api.Send<JsonElement>(HttpMethod.Post, $"api/chats/{verifiedRoom}/name", new { name = "" });
        toast?.Close(); var wasQuiet = DoNotDisturb.IsChecked; DoNotDisturb.IsChecked = true; ShowToast("검증", "방해 금지", "chat");
        if (toast is not null) throw new InvalidOperationException("방해 금지 중 알림 표시");
        DoNotDisturb.IsChecked = false; var soundOff = Quiet.IsChecked; Quiet.IsChecked = true; ShowToast("안 읽은 메시지 3개", "업무 메시지 2 · 채팅 1", "chat");
        if (toast is null) throw new InvalidOperationException("우측 하단 알림 표시 실패");
        await Task.Delay(100); SavePreview(toast, Path.Combine(directory, "toast-preview.png")); toast.Close();
        DoNotDisturb.IsChecked = wasQuiet; Quiet.IsChecked = soundOff;
        MutedRooms.Add(verifiedRoom); SavePreferences();
        if (JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(LocalDirectory, "preferences.json")))!.MutedRooms?.Contains(verifiedRoom) != true) throw new InvalidOperationException("방 알림 끄기 저장 실패");
        MutedRooms.Remove(verifiedRoom); SavePreferences();
        chat.Close();
        var survey = new SurveyWindow(this, people, [Api.Session!.User!.Id]); survey.Show();
        survey.TitleBox.Text = "교무회의 시간 조사"; survey.DescriptionBox.Text = "회의 시간을 선택해 주세요.";
        survey.QuestionsPanel.Children.Clear(); survey.ResetQuestionsForVerification();
        survey.AddQuestion("가능한 회의 시간", "choice", ["15시", "16시"]); survey.AddQuestion("추가 의견", "text");
        await survey.CreateSurvey();
        var created = (await Api.Get<SurveyItem[]>("api/surveys")).First(s => s.Title == "교무회의 시간 조사");
        await survey.ShowSurvey(created.Id); await survey.SubmitAnswers(["15시", "회의 자료를 미리 공유해 주세요."]);
        var result = await Api.Get<SurveyResults>($"api/surveys/{created.Id}/results");
        if (result.Responses.Length != 1 || result.Responses[0].Answers[0] != "15시") throw new InvalidOperationException("설문 UI 응답 실패");
        await Task.Delay(150); await survey.Reload();
        if ((survey.SurveyList.SelectedItem as SurveyItem)?.Id != created.Id) throw new InvalidOperationException("설문 알림 갱신 시 선택 상태 유지 실패");
        var formula = result with { Responses = [result.Responses[0] with { Answers = ["=1+1", "쉼표, 줄바꿈\n검증"] }] };
        if (!SurveyWindow.Csv(formula).Contains("'=1+1")) throw new InvalidOperationException("CSV 수식 차단 실패");
        survey.Tabs.SelectedIndex = 0; await Task.Delay(50); SavePreview(survey, Path.Combine(directory, "survey-preview.png")); survey.Tabs.SelectedIndex = 1; await Task.Delay(50); SavePreview(survey, Path.Combine(directory, "survey-results-preview.png"));
        survey.TitleBox.Text = "익명 만족도 조사"; survey.AnonymousBox.IsChecked = true; survey.ResetQuestionsForVerification(); survey.QuestionsPanel.Children.Clear();
        survey.AddQuestion("만족도", "choice", ["좋음", "보통"]); survey.AddQuestion("의견", "text");
        survey.NewSurveyForVerification(); await Task.Delay(50); SavePreview(survey, Path.Combine(directory, "survey-new-preview.png"));
        await survey.CreateSurvey();
        var nameless = (await Api.Get<SurveyItem[]>("api/surveys")).First(s => s.Title == "익명 만족도 조사");
        if (!nameless.Anonymous || survey.AnonymousBox.IsChecked == true) throw new InvalidOperationException("익명 설문 생성 실패");
        await survey.ShowSurvey(nameless.Id); await survey.SubmitAnswers(["좋음", "익명 의견입니다."]);
        var namelessDetail = await Api.Get<SurveyDetail>($"api/surveys/{nameless.Id}");
        if (namelessDetail.CanAnswer || !namelessDetail.Answered || namelessDetail.Answers is not null) throw new InvalidOperationException("익명 응답 상태 실패");
        var namelessResult = await Archive.KeepBallots(await Api.Get<SurveyResults>($"api/surveys/{nameless.Id}/results"), nameless.Id);
        if ((await Api.Get<SurveyResults>($"api/surveys/{nameless.Id}/results")).Responses.Length != 0) throw new InvalidOperationException("익명 응답이 서버에 남음");
        if (!namelessResult.Anonymous || namelessResult.Responses[0].Name.Length > 0 || SurveyWindow.Csv(namelessResult).Contains("이름") || !SurveyWindow.Csv(namelessResult).Contains("익명 의견입니다.")) throw new InvalidOperationException("익명 결과 또는 CSV 실패");
        survey.Tabs.SelectedIndex = 1; await Task.Delay(50); SavePreview(survey, Path.Combine(directory, "survey-anonymous-preview.png")); survey.Close();
        var frame = RemoteDesktop.Capture(); if (frame.Length > 262144 || !RemoteDesktop.ValidFrame(frame) || RemoteDesktop.InputSize != 40) throw new InvalidOperationException("원격 화면 또는 Windows 입력 구조 검증 실패");
        await VerifyPeerSecurity(frame);
        var preview = new RemoteClient(this);
        var window = new RemoteWindow(this, preview); window.Show(); window.Heading.Text = "원격 지원 화면 캡처 검증"; window.ScreenBorder.Visibility = Visibility.Visible; window.Frame(frame);
        SavePreview(window, Path.Combine(directory, "remote-preview.png")); window.Finish("검증 완료"); await preview.DisposeAsync();
        using var secondApi = new Api(Api.Address.ToString());
        await secondApi.Login("bob", Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!);
        await using var sharing = new RemoteClient(this, secondApi); await sharing.Connect();
        await remote!.Request(secondApi.Session!.User!.Id, false);
        for (var i = 0; i < 60 && sharing.Offer is null; i++) await Task.Delay(50);
        if (sharing.Offer is null) throw new InvalidOperationException("원격 요청 UI 수신 실패");
        await sharing.Accept(true);
        for (var i = 0; i < 300 && remote.SupportWindow?.ScreenImage.Source is null; i++) await Task.Delay(50);
        if (!remote.DirectConnected || !sharing.DirectConnected || !remote.CanControl || remote.SupportWindow?.ScreenImage.Source is null) throw new InvalidOperationException("원격 화면 PC 직접 연결 실패: " + remote.LastFailure + " / " + sharing.LastFailure + " / " + StatusLabel.Text);
        var physicalInput = Environment.GetEnvironmentVariable("SCHOOL_UI_SKIP_INPUT") != "1";
        var inputBox = new System.Windows.Controls.TextBox { Margin = new Thickness(18), FontSize = 18 };
        var inputWindow = new Window { Title = "원격 입력 검증", Width = 360, Height = 150, Content = inputBox, Topmost = true };
        try
        {
            if (physicalInput)
            {
            inputWindow.Show();
            var inputHandle = new System.Windows.Interop.WindowInteropHelper(inputWindow).Handle;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                inputWindow.Activate(); SetForegroundWindow(inputHandle); inputBox.Focus(); await Task.Delay(100);
                if (inputWindow.IsActive && inputBox.IsKeyboardFocused && GetForegroundWindow() == inputHandle) break;
            }
            if (!inputWindow.IsActive || !inputBox.IsKeyboardFocused || GetForegroundWindow() != new System.Windows.Interop.WindowInteropHelper(inputWindow).Handle) throw new InvalidOperationException("안전한 입력 검증 창에 포커스를 주지 못했습니다.");
            RemoteDesktop.Apply(new RemoteInput("text", Text: "직접"));
            for (var i = 0; i < 60 && inputBox.Text != "직접"; i++) await Task.Delay(50);
            if (inputBox.Text != "직접") throw new InvalidOperationException("Windows 로컬 입력 검증 실패: 길이=" + inputBox.Text.Length + ", 전경=" + (GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(inputWindow).Handle) + ", 입력크기=" + RemoteDesktop.InputSize);
            inputBox.Clear();
            await remote.Input(new RemoteInput("text", Text: "원격 입력 검증"));
            for (var i = 0; i < 60 && inputBox.Text != "원격 입력 검증"; i++) await Task.Delay(50);
            if (inputBox.Text != "원격 입력 검증") throw new InvalidOperationException("PC 직접 연결 및 Windows 실제 키보드 입력 실패: 길이=" + inputBox.Text.Length + ", 포커스=" + inputBox.IsKeyboardFocused + ", 제어=" + remote.CanControl + ", 권한=" + sharing.Offer?.Control + ", 번호=" + remote.Offer?.ControlRevision + "/" + sharing.Offer?.ControlRevision + ", 입력수=" + sharing.ReceivedInputCount + ", 적용=" + sharing.LastAppliedInput + ", 오류=" + remote.LastFailure + " / " + sharing.LastFailure);
            }
            await sharing.SetControl(false);
            for (var i = 0; i < 60 && remote.CanControl; i++) await Task.Delay(50);
            if (remote.CanControl) throw new InvalidOperationException("원격 제어 권한 해제 실패");
            await remote.Input(new RemoteInput("text", Text: "차단되어야 함")); await Task.Delay(150);
            if (physicalInput && inputBox.Text != "원격 입력 검증") throw new InvalidOperationException("권한 해제 후 입력 차단 실패");
        }
        finally { await remote.Stop(); inputWindow.Close(); }
        for (var i = 0; i < 60 && sharing.Offer is not null; i++) await Task.Delay(50);
        if (remote.DirectConnected || sharing.DirectConnected || sharing.Offer is not null) throw new InvalidOperationException("직접 연결 종료 실패");
        File.WriteAllText(Path.Combine(directory, "native-input-check.json"), JsonSerializer.Serialize(new { physicalInput }));
    }
    static async Task VerifyPeerSecurity(byte[] frame)
    {
        if (!RemoteClient.SafeInput(new RemoteInput("text", Text: "원격 입력 검증")) || !RemoteClient.SafeInput(new RemoteInput("release"))) throw new InvalidOperationException("정상 원격 입력 거부됨");
        if (RemoteClient.SafeInput(new RemoteInput("key", Value: 9999)) || RemoteClient.SafeInput(new RemoteInput("move", X: 2)) ||
            RemoteClient.SafeInput(new RemoteInput("text", Text: new string('x', 129))) || RemoteClient.SafeInput(new RemoteInput("unknown"))) throw new InvalidOperationException("원격 입력 검증 실패");
        using (var host = new PeerLink("bad-pin"))
        using (var viewer = new PeerLink("bad-pin"))
        using (var timeout = new System.Threading.CancellationTokenSource(5000))
        {
            host.Listen(true); var peer = new RemotePeer("bad-pin", "127.0.0.1", host.Port, host.Pin, viewer.Pin);
            var accepting = host.Connect(peer, true, timeout.Token);
            var rejected = false;
            try { await viewer.Connect(peer with { HostPin = new string('0', 64) }, false, timeout.Token); }
            catch (System.Security.Authentication.AuthenticationException) { rejected = true; }
            finally { timeout.Cancel(); try { await accepting; } catch (OperationCanceledException) { } }
            if (!rejected) throw new InvalidOperationException("다른 인증서 지문 차단 실패");
        }
        using var sender = new PeerLink("protocol"); using var receiver = new PeerLink("protocol");
        using var deadline = new System.Threading.CancellationTokenSource(10000);
        sender.Listen(true); var connection = new RemotePeer("protocol", "127.0.0.1", sender.Port, sender.Pin, receiver.Pin);
        await Task.WhenAll(sender.Connect(connection, true, deadline.Token), receiver.Connect(connection, false, deadline.Token));
        using (var reading = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            byte[]? received = null;
            var read = receiver.Receive(false, (_, data) => { received = data; reading.Cancel(); return Task.CompletedTask; }, reading.Token);
            await sender.Send(1, frame, deadline.Token); await read;
            if (received is null || !received.SequenceEqual(frame)) throw new InvalidOperationException("직접 TLS 화면 전송 검증 실패");
        }
        using (var reading = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        {
            PeerInput? received = null; var input = new PeerInput(7, new RemoteInput("text", Text: "직접 입력 확인"));
            var read = sender.Receive(true, (_, data) => { received = JsonSerializer.Deserialize<PeerInput>(data); reading.Cancel(); return Task.CompletedTask; }, reading.Token);
            await receiver.Send(2, JsonSerializer.SerializeToUtf8Bytes(input), deadline.Token); await read;
            if (received != input || !RemoteClient.SafeInput(received.Input)) throw new InvalidOperationException("직접 TLS 입력 전송 검증 실패");
        }
        var oversized = false;
        try { await receiver.Send(2, new byte[4097], deadline.Token); } catch (InvalidOperationException) { oversized = true; }
        if (!oversized) throw new InvalidOperationException("직접 연결 입력 크기 제한 실패");
        var forbidden = sender.Receive(true, (_, _) => throw new InvalidOperationException("검증 전에 처리됨"), deadline.Token);
        await receiver.Send(1, new byte[5000], deadline.Token);
        var bounded = false;
        try { await forbidden; } catch (InvalidOperationException error) { bounded = error.Message == "원격 데이터 크기가 잘못되었습니다."; }
        if (!bounded) throw new InvalidOperationException("직접 연결 수신 크기 제한 실패");
        sender.Dispose(); var ended = false;
        try { await receiver.Receive(false, (_, _) => Task.CompletedTask, deadline.Token); } catch (IOException) { ended = true; }
        if (!ended) throw new InvalidOperationException("직접 연결 단절 감지 실패");
    }
}
