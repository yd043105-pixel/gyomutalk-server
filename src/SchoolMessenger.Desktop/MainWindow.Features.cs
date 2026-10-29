using System.Text.Json;
using System.Windows;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    RemoteClient? remote;
    string notificationTarget = "message";
    public event Action<string>? ChatChanged;
    public event Action<string>? SurveyChanged;
    void NotifyFeature(string text, string target)
    { if (!DoNotDisturb.IsChecked) { notificationTarget = target; tray.ShowBalloonTip(4000, "온라인 교무실", text, System.Windows.Forms.ToolTipIcon.Info); if (!Quiet.IsChecked) System.Media.SystemSounds.Asterisk.Play(); } }
    void OpenNotification()
    { if (notificationTarget == "chat") OpenChat(); else if (notificationTarget == "survey") OpenSurvey(); else if(notificationTarget=="timetable"&&Api is not null)new TimetableWindow(this).Show();else OpenMailbox("unread"); }
    void OpenChatClick(object sender, RoutedEventArgs e) => OpenChat();
    void OpenSurveyClick(object sender, RoutedEventArgs e) => OpenSurvey();
    void OpenWorkClick(object sender, RoutedEventArgs e) { if (Api is not null) new WorkWindow(this).Show(); }
    void OpenAnnouncementClick(object sender, RoutedEventArgs e) { if (Api is not null) new AnnouncementWindow(this).Show(); }
    void OpenTimetableClick(object sender,RoutedEventArgs e){if(Api is not null)new TimetableWindow(this).Show();}
    void OpenCounselingClick(object sender,RoutedEventArgs e){if(Api is not null)new CounselingWindow(this).Show();}
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
        var collection = new ComposeWindow(this, people, other.Id, "Windows 제출 요청 검증"); collection.Show(); collection.BodyBox.Text = "점검 자료를 제출해 주세요."; collection.CollectFiles.IsChecked = true; collection.SubmissionDate.SelectedDate = DateTime.Today.AddDays(7);
        collection.SendButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        JsonElement[] collectionRequests = [];
        for (var i = 0; i < 100; i++) { collectionRequests = await Api!.Get<JsonElement[]>("api/submission-requests"); if (collectionRequests.Any(r => r.GetProperty("title").GetString() == "Windows 제출 요청 검증")) break; await Task.Delay(50); }
        var collectionId = collectionRequests.FirstOrDefault(r => r.GetProperty("title").GetString() == "Windows 제출 요청 검증");
        if (collectionId.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("Windows 쪽지 제출 요청 생성 실패");
        await work.Submission(Environment.GetEnvironmentVariable("YY_SUBMISSION_PREVIEW_ID")??collectionId.GetProperty("id").GetString()!); SavePreview(work, Path.Combine(directory, "submissions-preview.png")); work.Close();
        var chat = new ChatWindow(this, people); chat.Show();
        await chat.CreateRoom([other.Id], "UI 검증 대화방");
        for (var i = 0; i < 30 && chat.RoomId is null; i++) await Task.Delay(50);
        chat.BodyBox.Text = "교직원 채팅 창에서 실제로 보낸 대화입니다."; await chat.SendMessage();
        var entries = await Api!.Get<ChatEntry[]>($"api/chats/{chat.RoomId}/messages");
        if (!entries.Any(m => m.Body.Contains("실제로 보낸"))) throw new InvalidOperationException("채팅 UI 전송 실패");
        SavePreview(chat, Path.Combine(directory, "chat-preview.png")); chat.Close();
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
        SavePreview(survey, Path.Combine(directory, "survey-preview.png")); survey.Tabs.SelectedIndex = 1; SavePreview(survey, Path.Combine(directory, "survey-results-preview.png")); survey.Close();
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
