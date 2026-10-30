using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace SchoolMessenger.Desktop;
public partial class MainWindow : Window
{
    public Api? Api { get; private set; }
    public string LocalDirectory { get; } = Environment.GetEnvironmentVariable("SCHOOL_UI_LOCALDIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetCommandLineArgs().Contains("--server-manager")?"YeoyangMessengerServer/Messenger":"SchoolMessenger");
    public HashSet<string> Favorites { get; set; } = new();
    readonly HashSet<string> selectedPeople = new(), knownIncoming = new();
    readonly HashSet<string> collapsedGroups = new();
    readonly HashSet<long> knownTimetables = new();
    Person[] people = [];
    string schoolName="학교";
    bool favoritesOnly, onlineOnly, awayOnly, onlineFirst = true, schoolExpanded = true, refreshing, exiting;
    int unreadCount, peopleRefreshVersion;
    HubConnection? hub;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly System.Windows.Forms.NotifyIcon tray;
    readonly WindowsStartup startup;
    readonly SavedLogin savedLogin;
    bool startupReady, loginOptionsReady, signingIn;
    ServerManagementWindow? administratorWindow;
    public MainWindow()
    {
        InitializeComponent();
        CertificatePins.UseFolder(LocalDirectory);
        InitializeActivity();
        InitializeNavigation();
        MinHeight = Math.Min(580, SystemParameters.WorkArea.Height - 30);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 30);
        Directory.CreateDirectory(LocalDirectory);
        savedLogin = new SavedLogin(LocalDirectory);
        startup = new WindowsStartup(Environment.GetCommandLineArgs().Any(a=>a is "--verify-ui" or "--verify-admin" or "--verify-counseling" or "--verify-electives")
            ? @"Software\YeoyangSchoolMessenger\StartupTests\" + Path.GetFileName(LocalDirectory.TrimEnd(Path.DirectorySeparatorChar)) + Guid.NewGuid().ToString("N")
            : @"Software\Microsoft\Windows\CurrentVersion\Run",Environment.GetCommandLineArgs().Contains("--server-manager")?"YeoyangMessengerServerManager":"YeoyangSchoolMessenger",Environment.GetCommandLineArgs().Contains("--server-manager")?LocalServerWindow.StartupCommand:null);
        AutoStartCheck.IsChecked = AutoStartMenu.IsChecked = startup.Enabled;
        startupReady = true;
        try
        {
            var file = Path.Combine(LocalDirectory, "preferences.json");
            if (File.Exists(file) && JsonSerializer.Deserialize<Preferences>(File.ReadAllText(file)) is { } preferences)
            { ServerAddress.Text = connectedAddress = preferences.ServerAddress; UsernameBox.Text = preferences.Username; Quiet.IsChecked = preferences.Quiet; DoNotDisturb.IsChecked = preferences.DoNotDisturb; RememberLogin.IsChecked = preferences.AutoLogin; Favorites = preferences.Favorites.ToHashSet(); Appearance.Apply(preferences.ThemeId, preferences.Glass); SetAutomaticAway(preferences.IdleMinutes); }
        }
        catch (Exception error) when (error is IOException or JsonException) { LoginError.Text = "설정을 읽지 못했습니다. 서버 주소를 확인하세요."; }
        loginOptionsReady = true;
        tray = new System.Windows.Forms.NotifyIcon { Icon = Branding.TrayIcon, Text = "교무톡 · 온라인 교무실", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("교무실 열기", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(CloseApp)); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow); tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(OpenNotification);
        timer.Tick += async (_, _) => await Run(async () =>
        {
            if (Workspace.Visibility != Visibility.Visible) { if (RememberLogin.IsChecked == true) await TryAutomaticLogin(); return; }
            if (Api is null || refreshing) return;
            await Api.RefreshSession();
            if (Api.Session?.User is null) { await Disconnect(); LoginError.Text = "로그인 만료. 다시 로그인하세요."; return; }
            await Refresh();
            if (hub?.State == HubConnectionState.Disconnected) await ConnectNotifications();
        });
        Loaded += async (_, _) =>
        {
            if(Environment.GetCommandLineArgs().Contains("--verify-electives")){await VerifyElectivesUi();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-admin")){await VerifyAdministratorUi();return;}
            if(Environment.GetCommandLineArgs().Contains("--verify-counseling")){await VerifyCounselingUi();return;}
            if (Environment.GetCommandLineArgs().Contains("--verify-ui")) { await VerifyUi(); return; }
            if (startup.Enabled) { try { startup.SetEnabled(true); } catch (Exception error) { LoginError.Text = "자동 실행 설정을 갱신하지 못했습니다: " + error.Message; } }
            if (RememberLogin.IsChecked == true) { await TryAutomaticLogin(); if (savedLogin.Exists) timer.Start(); }
        };
    }
    public void SavePreferences() => File.WriteAllText(Path.Combine(LocalDirectory, "preferences.json"), JsonSerializer.Serialize(new Preferences(ServerAddress.Text, UsernameBox.Text, Quiet.IsChecked, Favorites.ToArray(), DoNotDisturb.IsChecked, RememberLogin.IsChecked == true, Appearance.Selected, Appearance.Glass,AutomaticAwayMinutes)));
    void OpenAppearance(object sender, RoutedEventArgs e) => new AppearanceWindow(this).ShowDialog();
    public void RefreshAppearance()
    {
        AllFilter.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, onlineOnly||awayOnly ? "Soft" : "Pale");
        AllFilter.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, onlineOnly||awayOnly ? "Muted" : "Blue");
        OnlineFilter.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, onlineOnly ? "Pale" : "Soft");
        OnlineFilter.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, onlineOnly ? "Blue" : "Muted");
        AwayFilter.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, awayOnly ? "Pale" : "Soft");
        AwayFilter.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, awayOnly ? "Blue" : "Muted");
        RosterTitle.Text=favoritesOnly?"즐겨찾기":"교직원";
        OrganizationButton.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, favoritesOnly ? "Glass" : "Blue");
        FavoritesButton.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, favoritesOnly ? "Blue" : "Glass");
        OrganizationButton.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, favoritesOnly ? "Muted" : "OnAccent");
        FavoritesButton.SetResourceReference(System.Windows.Controls.Button.ForegroundProperty, favoritesOnly ? "OnAccent" : "Muted");
        RenderTree();
    }
    void RememberChanged(object sender, RoutedEventArgs e)
    {
        if (!loginOptionsReady) return;
        if (RememberLogin.IsChecked != true) savedLogin.Clear();
        SavePreferences();
    }
    async void RegisterClick(object sender, RoutedEventArgs e)
    {
        try { await RequireServerAddress(); new RegistrationWindow(ServerAddress.Text) { Owner = this }.ShowDialog(); }
        catch (Exception error) { LoginError.Text = error.Message; }
    }
    void StartupChanged(object sender, RoutedEventArgs e)
    {
        if (!startupReady) return;
        var enabled = sender is MenuItem menu ? menu.IsChecked : AutoStartCheck.IsChecked == true;
        startupReady = false;
        try { startup.SetEnabled(enabled); AutoStartCheck.IsChecked = AutoStartMenu.IsChecked = enabled; }
        catch (Exception error)
        {
            AutoStartCheck.IsChecked = AutoStartMenu.IsChecked = !enabled;
            LoginError.Text = StatusLabel.Text = "자동 실행 설정 실패: " + error.Message;
        }
        finally { startupReady = true; }
    }
    async void LoginClick(object sender, RoutedEventArgs e)
    {
        if (signingIn||findingServer) return; signingIn = true;
        LoginButton.IsEnabled = false; LoginError.Text = "연결 중…";
        try { await SignIn(); } catch (Exception error) { LoginError.Text = error.Message; }
        finally { LoginButton.IsEnabled = true; signingIn = false; }
    }
    async Task SignIn()
    {
        try { await SignInOnce(); }
        catch (HttpRequestException) when (CertificatePins.Pending(ServerAddress.Text) is { } fingerprint && ConfirmCertificate(fingerprint))
        { await SignInOnce(); }
    }
    bool ConfirmCertificate(string fingerprint)
    {
        var answer = MessageBox.Show(this, "이 학교 서버에 처음 연결합니다.\n\n연결 확인 코드: " + CertificatePins.Code(fingerprint) + "\n\n서버 PC의 관리자 프로그램(또는 학교 관리자 안내)에 표시된 코드와 똑같을 때만 '예'를 누르세요. 다르면 '아니요'를 누르고 관리자에게 알려 주세요.", "학교 서버 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return false;
        CertificatePins.Trust(ServerAddress.Text, fingerprint); return true;
    }
    async Task SignInOnce()
    {
        await RequireServerAddress();
        if (hub is not null) await hub.DisposeAsync(); Api?.Dispose(); Api = new Api(ServerAddress.Text);
        await Api.Login(UsernameBox.Text.Trim(), PasswordBox.Password, RememberLogin.IsChecked == true); PasswordBox.Clear(); UsernameBox.Text=Api.Session!.User!.Username; SavePreferences();
        if(Api.Session!.User!.ActivationRequired){savedLogin.Clear();new StaffActivationWindow(Api){Owner=this}.ShowDialog();await Api.RefreshSession();throw new InvalidOperationException("계정 활성화 후 다시 로그인하세요.");}
        if(Api.Session.User.PasswordChangeRequired){savedLogin.Clear();ShowPasswordChange(true);throw new InvalidOperationException("개인 비밀번호로 변경한 뒤 다시 로그인하세요.");}
        if (RememberLogin.IsChecked == true) savedLogin.Save(Api); else savedLogin.Clear();
        await EnterWorkspace();
    }
    async Task EnterWorkspace()
    {
        if(Api?.Session?.User?.ActivationRequired==true){savedLogin.Clear();new StaffActivationWindow(Api){Owner=this}.ShowDialog();throw new InvalidOperationException("계정 활성화 후 다시 로그인하세요.");}
        if(Api?.Session?.User?.PasswordChangeRequired==true){savedLogin.Clear();ShowPasswordChange(true);throw new InvalidOperationException("개인 비밀번호로 변경한 뒤 다시 로그인하세요.");}
        var user = Api!.Session!.User!; IdentityLabel.Text = user.Name + " 선생님"; IdentityDepartment.Text = string.Join(" · ",new[]{user.Department,user.Position}.Where(s=>s.Length>0)); archive=null;
        schoolName=(await Api.Get<SchoolMessenger.Contracts.RegistrationPolicySet>("api/registration/policy")).Operator;Title=schoolName+" · 온라인 교무실";
        AdminButton.Visibility = user.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible; LoginPanel.Visibility = Visibility.Collapsed;
        knownTimetables.Clear();
        knownIncoming.Clear(); foreach (var message in await Api.Get<MessageItem[]>("api/messages?box=received")) knownIncoming.Add(message.Id);
        await Refresh(); await ConnectNotifications(); await RefreshPeople(); timer.Start(); activityTimer.Start();
        remote = new RemoteClient(this);
        try { await remote.Connect(); } catch { StatusLabel.Text = "원격 지원 연결 대기 중"; }
        ShowUpdates();
        if(user.IsAdmin)OpenAdministrator();
    }
    async Task TryAutomaticLogin()
    {
        if (signingIn || !savedLogin.Exists || RememberLogin.IsChecked != true) return;
        signingIn = true; LoginButton.IsEnabled = false; LoginError.Text = "자동 로그인 중…";
        try
        {
            await RequireServerAddress();
            Api?.Dispose(); Api = new Api(ServerAddress.Text);
            if (!savedLogin.Restore(Api, UsernameBox.Text.Trim())) { LoginError.Text = "자동 로그인 기간이 끝났습니다. 다시 로그인하세요."; return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Api.RefreshSession(timeout.Token);
            if (Api.Session?.User is null) { savedLogin.Clear(); timer.Stop(); LoginError.Text = "계정이 변경되었거나 자동 로그인이 만료되었습니다. 다시 로그인하세요."; return; }
            await EnterWorkspace(); LoginError.Text = "";
        }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or JsonException)
        { savedLogin.Clear(); timer.Stop(); LoginError.Text = "저장된 자동 로그인 정보를 읽지 못했습니다. 다시 로그인하세요."; }
        catch (Exception error) { LoginError.Text = "학교 서버 연결을 기다리는 중입니다. 30초 후 다시 시도합니다. " + error.Message; }
        finally { LoginButton.IsEnabled = true; signingIn = false; }
    }
    public MessageArchive Archive=>archive??=new MessageArchive(LocalDirectory,Api!);
    MessageArchive? archive;
    async Task ConnectNotifications()
    {
        if (Api is null) return;
        if (hub is not null) await hub.DisposeAsync();
        hub = new HubConnectionBuilder().WithUrl(new Uri(Api.Address, "hub"), o => { o.Cookies = Api.Cookies; CertificatePins.Apply(o, Api.Address); }).WithAutomaticReconnect().Build();
        hub.On<string>("NewMessage", async id => await Dispatcher.InvokeAsync(() => Run(async () => { if (knownIncoming.Add(id)) Notify(); await Refresh(); })).Task.Unwrap());
        hub.On<string>("MessageRecalled", async id => await Dispatcher.InvokeAsync(() => Run(async()=>{Archive.Remove(id);await Refresh();})).Task.Unwrap());
        hub.On<string>("ReadChanged", async _ => await Dispatcher.InvokeAsync(() => Run(RefreshWindows)).Task.Unwrap());
        hub.On("PresenceChanged", async () => await Dispatcher.InvokeAsync(() => Run(RefreshPeople)).Task.Unwrap());
        hub.On<string>("ChatChanged", id => Dispatcher.InvokeAsync(() => { ChatChanged?.Invoke(id); NotifyFeature("새 채팅을 확인하세요.", "chat"); }).Task);
        hub.On<string>("SurveyChanged", id => Dispatcher.InvokeAsync(() => { SurveyChanged?.Invoke(id); NotifyFeature("설문 변경 사항을 확인하세요.", "survey"); }).Task);
        hub.On<string,long>("TimetableChanged",(date,revision)=>Dispatcher.InvokeAsync(()=>{if(knownTimetables.Add(revision))NotifyFeature("시간표 변경 · "+date,"timetable");}).Task);
        hub.Reconnecting += _ => { Dispatcher.Invoke(() => ConnectionLabel.Text = "재연결 중 · 수신 확인 계속"); return Task.CompletedTask; };
        hub.Reconnected += _ => Dispatcher.InvokeAsync(() => Run(async()=>{sentAway=null;await SynchronizeActivity();await Refresh();})).Task.Unwrap();
        hub.Closed += _ => { Dispatcher.Invoke(() => ConnectionLabel.Text = "알림 연결 끊김 · 재시도 중"); return Task.CompletedTask; };
        try { await hub.StartAsync(); sentAway=null;await SynchronizeActivity();ConnectionLabel.Text = "학교 서버 연결됨"; }
        catch { ConnectionLabel.Text = "실시간 알림 연결 실패 · 수신 확인 30초"; }
    }
    async Task Refresh()
    {
        if (Api is null || refreshing) return;
        refreshing = true;
        try
        {
            await RefreshNavigationAccess();
            await Archive.Synchronize();
            await RefreshPeople(); await UpdateUnread();
            var incoming = await Api.Get<MessageItem[]>("api/messages?box=received");
            if (incoming.Any(m => !knownIncoming.Contains(m.Id))) Notify();
            foreach (var message in incoming) knownIncoming.Add(message.Id);
            foreach(var n in await Api.Get<SchoolMessenger.Contracts.TimetableNotice[]>("api/timetable/notices"))
                if(knownTimetables.Add(n.Revision)&&n.ReadAt is null)NotifyFeature(n.Title+" · "+n.Date,"timetable");
            await RefreshWindows();
            ConnectionLabel.Text = hub?.State == HubConnectionState.Connected ? "학교 서버 연결됨" : "학교 서버 연결됨 · 수신 확인 30초";
        }
        finally { refreshing = false; }
    }
    async Task RefreshWindows()
    {
        foreach (var window in OwnedWindows.OfType<MailboxWindow>().ToArray()) await window.Reload();
        foreach (var window in OwnedWindows.OfType<ChatWindow>().ToArray()) await window.Refresh();
        foreach (var window in OwnedWindows.OfType<SurveyWindow>().ToArray()) await window.Reload();
        foreach (var window in OwnedWindows.OfType<WorkWindow>().ToArray()) await window.Reload();
        if (remote is not null) { try { await remote.Connect(); } catch { } }
    }
    public async Task UpdateUnread()
    {
        if (Api is null) return;
        var unread = await Api.Get<MessageItem[]>("api/messages?box=unread");
        unreadCount = unread.Length; UpdateRosterHint();
        UnreadShortcut.Content=$"미확인 {unreadCount}{(unreadCount==100?"+":"")}";
    }
    async Task RefreshPeople()
    {
        var api = Api; if (api is null) return;
        var version = ++peopleRefreshVersion;
        var updated = await api.Get<Person[]>("api/users");
        if (api != Api || version != peopleRefreshVersion) return;
        people = updated;
        selectedPeople.IntersectWith(people.Select(p => p.Id)); RenderTree(); UpdateRosterHint();
    }
    void UpdateRosterHint()
    {
        RosterHint.Text = $"온라인 {people.Count(p=>p.Online&&!p.Away)} · 휴대폰 {people.Count(p=>!p.Online&&p.Mobile)} · 자리비움 {people.Count(p=>p.Online&&p.Away)} · 오프라인 {people.Count(p=>!p.Online&&!p.Mobile)}";
        AllFilter.Content = $"전체 {people.Length}"; OnlineFilter.Content = $"접속 중 {people.Count(p => p.Available)}"; AwayFilter.Content=$"자리비움 {people.Count(p=>p.Online&&p.Away)}";
        OnlineFilter.ToolTip="PC 온라인 또는 교사용 휴대폰 앱 접속 중";AwayFilter.ToolTip="PC에서 입력이 없거나 화면이 잠긴 교직원";
    }
    void UpdateSelectionHint() => SelectionHint.Text = $"선택 {selectedPeople.Count}명";
    void RenderTree()
    {
        if (OrganizationTree is null || PeopleSearch is null) return;
        var selected = (OrganizationTree.SelectedItem as TreeViewItem)?.Tag as Person;
        void Remember(ItemsControl parent) { foreach (var row in parent.Items.OfType<TreeViewItem>()) { if (row.Tag is string key) { if (key == "school") schoolExpanded = row.IsExpanded; else if (row.IsExpanded) collapsedGroups.Remove(key); else collapsedGroups.Add(key); } Remember(row); } }
        Remember(OrganizationTree);
        OrganizationTree.Items.Clear();
        var filtered = people.Where(p => p.Label.Contains(PeopleSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase) && (!favoritesOnly || Favorites.Contains(p.Id)) && (!onlineOnly || p.Available)&&(!awayOnly||p.Online&&p.Away)).ToArray();
        var school = GroupRow(schoolName, "school", filtered.Select(p => p.Id).ToArray(), schoolExpanded || PeopleSearch.Text.Length > 0); OrganizationTree.Items.Add(school);
        if (filtered.Length == 0) school.Items.Add(new TreeViewItem { Header = favoritesOnly ? "즐겨찾기를 추가하세요." : "검색 결과가 없습니다.", IsEnabled = false });
        foreach (var group in filtered.GroupBy(p => p.Department).OrderBy(g => g.Key))
        {
            var row = GroupRow(group.Key, group.Key, group.Select(p => p.Id).ToArray(), !collapsedGroups.Contains(group.Key) || PeopleSearch.Text.Length > 0);
            school.Items.Add(row);
            foreach (var person in group.OrderBy(p => onlineFirst?(p.Online?(p.Away?2:0):p.Mobile?1:3):0).ThenBy(p => p.Name))
            {
                var check = new System.Windows.Controls.CheckBox { IsChecked = selectedPeople.Contains(person.Id), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                check.Checked += (_, _) => { selectedPeople.Add(person.Id); UpdateSelectionHint(); }; check.Unchecked += (_, _) => { selectedPeople.Remove(person.Id); UpdateSelectionHint(); };
                var panel = new Grid { MinHeight = 36,ToolTip=person.Presence };
                ToolTipService.SetInitialShowDelay(panel,500);
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.ColumnDefinitions.Add(new ColumnDefinition()); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                panel.Children.Add(check);
                var status = PresenceMark(person.Online,person.Away,person.Mobile);status.Margin=new Thickness(7,0,10,0);
                Grid.SetColumn(status,1);panel.Children.Add(status);
                var name = new TextBlock { Text = person.Name + (Favorites.Contains(person.Id) ? " ★" : ""), FontSize = 13, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("Ink"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                Grid.SetColumn(name, 2); panel.Children.Add(name);
                var position = new TextBlock { Text = person.Position, FontSize = 10, MaxWidth = 72, Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = person.Position };
                position.SetResourceReference(TextBlock.ForegroundProperty, "Muted");Grid.SetColumn(position,3);panel.Children.Add(position);
                var contact = new TreeViewItem { Header = panel, Tag = person, ContextMenu = PersonMenu(person) };
                contact.PreviewMouseRightButtonDown += (_, _) => contact.IsSelected = true;
                contact.MouseDoubleClick += async (_, e) => { if (e.ChangedButton != MouseButton.Left) return; e.Handled = true; await WriteMessage(person.Id); };
                row.Items.Add(contact); if (selected?.Id == person.Id) contact.IsSelected = true;
            }
        }
        UpdateSelectionHint();
    }
    TreeViewItem GroupRow(string label, string key, string[] ids, bool expanded)
    {
        var check = new System.Windows.Controls.CheckBox { Content = label + (key == "school" ? "" : $"  · {ids.Length}명"), FontSize = 11, FontWeight = FontWeights.SemiBold, IsChecked = ids.Length > 0 && ids.All(selectedPeople.Contains), Margin = new Thickness(0, 2, 0, 2) };
        check.Checked += (_, _) => { foreach (var id in ids) selectedPeople.Add(id); RenderTree(); };
        check.Unchecked += (_, _) => { foreach (var id in ids) selectedPeople.Remove(id); RenderTree(); };
        return new TreeViewItem { Header = check, Tag = key, IsExpanded = expanded };
    }
    internal ContextMenu PersonMenu(Person person)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = person.Presence, IsEnabled = false });
        menu.Items.Add(new Separator());
        void Add(string text, RoutedEventHandler action) { var key = text.StartsWith("메시지 보내기") ? "MailIcon" : text.StartsWith("파일") ? "FileIcon" : text.StartsWith("메시지 관리") ? "HistoryIcon" : text.StartsWith("사용자") ? "UserIcon" : "StarIcon"; var icon = new System.Windows.Shapes.Path { Data = (Geometry)FindResource(key), StrokeThickness = 1.5, Stretch = Stretch.Uniform, Width = 15, Height = 15 }; icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Blue"); var item = new MenuItem { Header = text, Icon = icon }; item.Click += action; menu.Items.Add(item); }
        Add("메시지 보내기(_M)", async (_, _) => await WriteMessage(person.Id));
        Add("파일 보내기(_F)", async (_, _) => await WriteMessage(person.Id, filesOnly: true));
        Add("채팅하기(_C)", (_, _) => OpenChat([person.Id]));
        Add("설문 보내기(_V)", (_, _) => OpenSurvey([person.Id]));
        if (person.Online && person.Id != Api?.Session?.User?.Id)
        {
            Add("원격 지원하기", async (_, _) => await Run(() => RequestRemote(person.Id, false)));
            Add("원격 지원받기", async (_, _) => await Run(() => RequestRemote(person.Id, true)));
        }
        menu.Items.Add(new Separator());
        Add("메시지 관리함(_H)", (_, _) => OpenMailbox("conversation", person));
        Add("사용자 정보보기(_I)", (_, _) => System.Windows.MessageBox.Show(this, $"이름: {person.Name}\n부서: {person.Department}\n직책: {person.Position}\n상태: {person.Presence}", "사용자 정보", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(new Separator());
        Add(Favorites.Contains(person.Id) ? "즐겨찾기 해제" : "즐겨찾기 추가", (_, _) => { if (!Favorites.Add(person.Id)) Favorites.Remove(person.Id); SavePreferences(); RenderTree(); });
        return menu;
    }
    async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { StatusLabel.Text = error.Message; } }
    void SearchPeople(object sender, TextChangedEventArgs e) => RenderTree();
    void ToggleSort(object sender, RoutedEventArgs e) { onlineFirst = !onlineFirst; SortButton.Content = onlineFirst ? "이름순 ↕" : "접속순 ↕"; SortButton.ToolTip = onlineFirst ? "현재 접속자 우선 · 클릭하면 이름순" : "현재 이름순 · 클릭하면 접속자 우선"; RenderTree(); }
    void FilterAll(object sender, RoutedEventArgs e) { onlineOnly = awayOnly = false; UpdateFilters(); }
    void FilterOnline(object sender, RoutedEventArgs e) { onlineOnly = true;awayOnly=false; UpdateFilters(); }
    void FilterAway(object sender,RoutedEventArgs e){awayOnly=true;onlineOnly=false;UpdateFilters();}
    void UpdateFilters() => RefreshAppearance();
    void UpdateNavigation() => RefreshAppearance();
    void ShowOrganization(object sender, RoutedEventArgs e) { favoritesOnly = false; PeopleSearch.Clear(); UpdateNavigation(); }
    void ToggleFavorites(object sender, RoutedEventArgs e) { favoritesOnly = !favoritesOnly; UpdateNavigation(); }
    async void Compose(object sender, RoutedEventArgs e) => await WriteMessage();
    async void SendFilesClick(object sender, RoutedEventArgs e) => await WriteMessage(filesOnly: true);
    public async Task WriteMessage(string? recipient = null, string? title = null, bool filesOnly = false)
    {
        await Run(async () =>
        {
            if (Api is null) return;
            var selected = recipient is null ? selectedPeople.ToArray() : new[] { recipient };
            if (selected.Length == 0 && (OrganizationTree.SelectedItem as TreeViewItem)?.Tag is Person person) selected = [person.Id];
            var contacts = await Api.Get<Person[]>("api/users");
            var dialog = new ComposeWindow(this, contacts, recipient, title ?? (filesOnly ? "파일 전달" : null), selected);
            if (filesOnly) { dialog.BodyBox.Text = "첨부파일을 전달합니다."; dialog.Loaded += (_, _) => dialog.ChooseFiles(); }
            dialog.ShowDialog(); SavePreferences(); await Refresh();
        });
    }
    void OpenMailboxClick(object sender, RoutedEventArgs e) => OpenMailbox((string)((System.Windows.Controls.Button)sender).Tag);
    internal MailboxWindow OpenMailbox(string box, Person? person = null)
    { var window = new MailboxWindow(this, box, person); window.Show(); return window; }
    void OpenSettings(object sender, RoutedEventArgs e) { var button = (System.Windows.Controls.Button)sender; button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true; }
    void PasswordKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) LoginClick(sender, e); }
    void OpenAdmin(object sender, RoutedEventArgs e)=>OpenAdministrator();
    void OpenAdministrator()
    {
        if(Api?.Session?.User?.IsAdmin!=true)return;
        if(administratorWindow is{IsVisible:true}){administratorWindow.Activate();return;}
        administratorWindow=new ServerManagementWindow(this,Api);administratorWindow.Closed+=(_,_)=>administratorWindow=null;administratorWindow.Show();
    }
    void OpenOfficeClick(object sender,RoutedEventArgs e){if(Api is not null)new OfficeWindow(this).Show();}
    async void OpenPolicies(object sender,RoutedEventArgs e)
    {
        if(Api is null)return;
        try
        {
            var policy=await Api.Get<SchoolMessenger.Contracts.RegistrationPolicySet>("api/registration/policy");
            var panel=new StackPanel{Margin=new Thickness(20)};panel.Children.Add(new TextBlock{Text=policy.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
            var window=new Window{Owner=this,Title="약관·개인정보 안내",Width=420,Height=300,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new ScrollViewer{Content=panel}};
            panel.Children.Add(new TextBlock{Text="처리 주체: "+policy.Operator+"\n문의: "+policy.Contact,TextWrapping=TextWrapping.Wrap});
            foreach(var document in policy.Documents){var button=new System.Windows.Controls.Button{Content=document.Title,Margin=new Thickness(0,4,0,4)};button.Click+=(_,_)=>RegistrationWindow.ShowDocument(window,policy,document);panel.Children.Add(button);}window.ShowDialog();
        }
        catch(Exception error){MessageBox.Show(this,error.Message,"약관 안내");}
    }
    void ChangePassword(object sender, RoutedEventArgs e)=>ShowPasswordChange();
    void ShowPasswordChange(bool required=false)
    {
        if (Api is null) return;
        var old = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 5, 0, 14) };
        var next = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 5, 0, 14) };
        var panel = new StackPanel { Margin = new Thickness(24) };
        if(required)panel.Children.Add(new TextBlock{Text="초기 비밀번호 12345678을 개인 비밀번호로 변경해야 업무 자료를 열 수 있습니다.",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock { Text = "현재 비밀번호" }); panel.Children.Add(old);
        panel.Children.Add(new TextBlock { Text = "새 비밀번호 · 12~128자" }); panel.Children.Add(next);
        var confirm=new System.Windows.Controls.PasswordBox{Margin=new Thickness(0,5,0,14)};panel.Children.Add(new TextBlock{Text="새 비밀번호 확인"});panel.Children.Add(confirm);
        var save = new System.Windows.Controls.Button { Content = "변경 후 다시 로그인" }; panel.Children.Add(save);
        var dialog = new Window { Title = "비밀번호 변경", Owner = this, Width = 380, Height = required?440:390, Content = panel, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        save.Click += async (_, _) => { save.IsEnabled = false; try {if(next.Password!=confirm.Password)throw new InvalidOperationException("새 비밀번호 확인이 일치하지 않습니다.");await Api.Send<JsonElement>(HttpMethod.Post, "api/password", new { currentPassword = old.Password, newPassword = next.Password }); dialog.Close(); await Disconnect(); } catch (Exception error) { System.Windows.MessageBox.Show(dialog, error.Message); } finally { save.IsEnabled = true; } };
        dialog.Closed+=(_,_)=>{old.Clear();next.Clear();confirm.Clear();};
        dialog.ShowDialog();
    }
    async void Logout(object sender, RoutedEventArgs e)
        => await SignOut();
    async Task SignOut()
    { try { if (Api is not null) await Api.Send<JsonElement>(HttpMethod.Post, "api/logout"); } catch (Exception error) { LoginError.Text = error.Message; } finally { RememberLogin.IsChecked = false; await Disconnect(); } }
    async Task Disconnect()
    {
        schoolName="학교";Title="교무톡 · 온라인 교무실";
        savedLogin.Clear();
        ElectivesButton.Visibility=Visibility.Collapsed;
        if (remote is not null) { await remote.DisposeAsync(); remote = null; }
        timer.Stop(); activityTimer.Stop();sentAway=null; foreach (var window in OwnedWindows.Cast<Window>().ToArray()) window.Close();
        if (hub is not null) { await hub.DisposeAsync(); hub = null; } Api?.Dispose(); Api = null;
        OrganizationTree.Items.Clear(); people = []; selectedPeople.Clear(); Workspace.Visibility = Visibility.Collapsed; LoginPanel.Visibility = Visibility.Visible;
    }
    void Notify() { if (DoNotDisturb.IsChecked) return; notificationTarget = "message"; tray.ShowBalloonTip(5000, "온라인 교무실", "새 업무 메시지가 도착했습니다.", System.Windows.Forms.ToolTipIcon.Info); if (!Quiet.IsChecked) System.Media.SystemSounds.Asterisk.Play(); }
    void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    void WindowClosing(object? sender, CancelEventArgs e)
    { if (!exiting) { e.Cancel = true; Hide(); } else { timer.Stop(); tray.Dispose(); SavePreferences(); } }
    async void CloseApp() { if (remote is not null) { await remote.DisposeAsync(); remote = null; } exiting = true; Close(); }
    void ExitApp(object sender, RoutedEventArgs e) => CloseApp();
    static void SavePreview(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var width = content.ActualWidth + content.Margin.Left + content.Margin.Right;
        var height = content.ActualHeight + content.Margin.Top + content.Margin.Bottom;
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen())
        {
            draw.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            draw.DrawRectangle(new VisualBrush(content), null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
    async Task VerifyUi()
    {
        try
        {
            VerifyUpdateNotice();
            var loginPreview=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")??"artifacts/client-preview.png"))!,"school-login.png");
            Directory.CreateDirectory(Path.GetDirectoryName(loginPreview)!);SavePreview(this,loginPreview);
            try { startup.SetEnabled(true); if (!startup.Enabled || !WindowsStartup.Command.EndsWith(" --startup", StringComparison.Ordinal)) throw new InvalidOperationException("자동 실행 등록 실패"); }
            finally { startup.SetEnabled(false); }
            if (startup.Enabled) throw new InvalidOperationException("자동 실행 해제 실패");
            ServerAddress.Text = Environment.GetEnvironmentVariable("SCHOOL_UI_SERVER") ?? ServerAddress.Text;
            if(!Uri.TryCreate(ServerAddress.Text,UriKind.Absolute,out var verificationAddress)||!verificationAddress.IsLoopback||verificationAddress.Scheme!="http")throw new InvalidOperationException("화면 검사는 로컬 HTTP 가상 서버만 사용합니다.");
            var verificationServer=ServerAddress.Text;
            foreach(var address in new[]{"https://192.0.2.10:5443","https://192.0.2.11:5443"})
            {
                PasswordBox.Password="synthetic-not-a-password";RememberLogin.IsChecked=true;Favorites.Add("synthetic-school-member");File.WriteAllText(savedLogin.FilePath,"synthetic-session-marker");
                ServerAddress.Text=address;
                if(savedLogin.Exists||PasswordBox.Password.Length!=0||RememberLogin.IsChecked==true||Favorites.Count!=0)throw new InvalidOperationException("학교 변경 시 이전 학교 로그인·즐겨찾기 분리 실패");
            }
            ServerAddress.Text=verificationServer;SavePreferences();
            UsernameBox.Text = Environment.GetEnvironmentVariable("SCHOOL_UI_USER") ?? "admin";
            RememberLogin.IsChecked = true;
            PasswordBox.Password = Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD") ?? ""; await SignIn();
            if(Environment.GetEnvironmentVariable("YY_SUBMISSION_UI_ONLY")=="1")
            {
                if(Api?.Session?.User is null)throw new InvalidOperationException("제출 화면 시운행 로그인 실패");
                var submissionOutput=Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")!))!;
                var work=new WorkWindow(this,Environment.GetEnvironmentVariable("YY_SUBMISSION_PREVIEW_ID"));work.Show();await work.VerifySubmissionLayout(SavePreview,submissionOutput);work.Close();await SignOut();exiting=true;System.Windows.Application.Current.Shutdown(0);return;
            }
            var saved = File.ReadAllBytes(savedLogin.FilePath);
            if (System.Text.Encoding.UTF8.GetString(saved).Contains(Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!)) throw new InvalidOperationException("비밀번호 저장 금지 검증 실패");
            var originalUser = Api!.Session!.User!.Id;
            await Disconnect(); File.WriteAllBytes(savedLogin.FilePath, saved); await TryAutomaticLogin();
            if (Api?.Session?.User?.Id != originalUser || Workspace.Visibility != Visibility.Visible || PasswordBox.Password.Length != 0) throw new InvalidOperationException("암호화 정보로 자동 로그인 복원 실패");
            var presenceDirectory=Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT")??"artifacts/client-preview.png"))!;
            Directory.CreateDirectory(presenceDirectory);await VerifyPresenceUi(presenceDirectory);
            if (Width > 460 || people.Length == 0) throw new InvalidOperationException("조직도 크기 또는 교직원 목록 오류");
            Person[] VisibleContacts()
            {
                var found = new List<Person>();
                void Visit(ItemsControl parent) { foreach (var row in parent.Items.OfType<TreeViewItem>()) { if (row.Tag is Person contact) found.Add(contact); Visit(row); } }
                Visit(OrganizationTree); return found.ToArray();
            }
            var me = Api!.Session!.User!;
            for (var i = 0; i < 30 && !people.Any(p => p.Id == me.Id && p.Online); i++) { await Task.Delay(50); await RefreshPeople(); }
            PeopleSearch.Text = me.Name;
            if (!VisibleContacts().Any(p => p.Id == me.Id) || VisibleContacts().Any(p => !p.Label.Contains(me.Name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("조직도 검색 실패");
            PeopleSearch.Clear(); OnlineFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (!VisibleContacts().Any() || VisibleContacts().Any(p => !p.Available)) throw new InvalidOperationException("접속 중 필터 실패");
            AllFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (VisibleContacts().Length != people.Length) throw new InvalidOperationException("전체 교직원 필터 실패");
            foreach(var contact in OrganizationTree.Items.OfType<TreeViewItem>().SelectMany(r=>r.Items.OfType<TreeViewItem>()).SelectMany(r=>r.Items.OfType<TreeViewItem>()))
            {
                if(contact.Tag is not Person person||contact.Header is not Grid panel)continue;
                var mark=panel.Children.OfType<Grid>().Single();
                if(mark.ToolTip?.ToString()!=person.Presence||ToolTipService.GetInitialShowDelay(mark)!=500)throw new InvalidOperationException("접속 상태 마우스 안내 표시 실패");
                if(!person.Online&&person.Mobile){if(mark.Children.OfType<System.Windows.Shapes.Ellipse>().Any())throw new InvalidOperationException("휴대폰 모양 표시 실패");}
                else{var dot=mark.Children.OfType<System.Windows.Shapes.Ellipse>().Single();if(dot.Width!=10||(!person.Online&&dot.Fill!=Brushes.Transparent))throw new InvalidOperationException("작은 접속 원 표시 실패");}
            }
            if (OrganizationTree.Items.OfType<TreeViewItem>().SelectMany(r => r.Items.OfType<TreeViewItem>()).Any(r => !r.IsExpanded)) throw new InvalidOperationException("필터 변경 시 부서 펼침 상태 유지 실패");
            if (System.Windows.Shell.WindowChrome.GetWindowChrome(this)?.CaptionHeight != 38) throw new InvalidOperationException("제목줄 적용 실패");
            var output = Environment.GetEnvironmentVariable("SCHOOL_UI_OUTPUT") ?? "artifacts/client-preview.png";
            var directory = Path.GetDirectoryName(Path.GetFullPath(output))!; Directory.CreateDirectory(directory);
            static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
            var navigationButtons=Descendants(Workspace).OfType<System.Windows.Controls.Button>().Where(b=>b.Style==(Style)FindResource("RailButton")).ToArray();
            if(Descendants(NavigationRail).OfType<ScrollViewer>().Any()||NavigationItems.Children.OfType<TextBlock>().Any())throw new InvalidOperationException("툴바 스크롤 또는 연락처 분류가 남아 있습니다.");
            if(((SolidColorBrush)MessageSection.Background).Color==((SolidColorBrush)SchoolSection.Background).Color)throw new InvalidOperationException("메시지·학교 업무 분류 색상 구분 실패");
            if(!ElectivesButton.IsVisible)throw new InvalidOperationException("관리자 수강신청 진입 표시 실패");
            foreach(var size in new[]{(Width:450d,Height:820d),(Width:450d,Height:760d),(Width:380d,Height:580d),(Width:450d,Height:820d)})
            {
                Width=size.Width;Height=size.Height;UpdateLayout();UpdateNavigationLayout();UpdateLayout();
                await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
                foreach(var button in navigationButtons)
                {
                    if(!button.IsVisible)continue;
                    var buttonBounds=button.TransformToAncestor(NavigationLayout).TransformBounds(new Rect(button.RenderSize));
                    if(buttonBounds.Top<-.5||buttonBounds.Bottom>NavigationLayout.ActualHeight+.5)throw new InvalidOperationException($"스크롤 없는 툴바 범위 초과 ({size.Width}×{size.Height})");
                    var icons=Descendants(button).OfType<System.Windows.Shapes.Path>().ToArray();
                    if(size.Width==380&&icons.Any(i=>i.IsVisible)||size.Width==450&&size.Height==820&&icons.Any(i=>!i.IsVisible))throw new InvalidOperationException("창 크기별 아이콘·텍스트 전환 실패");
                    foreach(var label in Descendants(button).OfType<TextBlock>().Where(t=>t.IsVisible))
                    {
                        var bounds=label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                        if(bounds.Left<button.Padding.Left-.5||bounds.Right>button.ActualWidth-button.Padding.Right+.5||bounds.Top<button.Padding.Top-.5||bounds.Bottom>button.ActualHeight-button.Padding.Bottom+.5)
                            throw new InvalidOperationException($"왼쪽 메뉴 글씨 잘림: {label.Text} ({size.Width}×{size.Height})");
                    }
                }
                SavePreview(this,Path.Combine(directory,$"navigation-{size.Width:0}x{size.Height:0}-preview.png"));
            }
            using(var ordinary=new Api(ServerAddress.Text))
            {
                await ordinary.Login("bob",Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!);
                var originalApi=Api;timer.Stop();
                try
                {
                    Api=ordinary;await RefreshNavigationAccess();if(ElectivesButton.IsVisible)throw new InvalidOperationException("일반 교사에게 수강신청 표시");
                    await originalApi!.Send<JsonElement>(HttpMethod.Post,"api/admin/elective-managers",new SchoolMessenger.Contracts.ElectiveManagerChange(ordinary.Session!.User!.Id,true));
                    await RefreshNavigationAccess();if(!ElectivesButton.IsVisible)throw new InvalidOperationException("담당자 지정 후 수강신청 표시 실패");
                    await originalApi.Send<JsonElement>(HttpMethod.Post,"api/admin/elective-managers",new SchoolMessenger.Contracts.ElectiveManagerChange(ordinary.Session.User.Id,false));
                    await RefreshNavigationAccess();if(ElectivesButton.IsVisible)throw new InvalidOperationException("담당자 해제 후 수강신청 숨김 실패");
                }
                finally{Api=originalApi;await RefreshNavigationAccess();timer.Start();}
            }
            File.WriteAllText(Path.Combine(directory,"navigation-ui-checks.json"),JsonSerializer.Serialize(new{noScroll=true,compactText=true,categoryColors=true,electiveRoleChecks=4,windowSizes=new[]{"450x820","450x760","380x580"},realSchoolData=false}));
            SavePreview(this, output);
            var originalTheme = Appearance.Selected; var originalGlass = Appearance.Glass;
            var themedInbox = OpenMailbox("received"); await themedInbox.Reload();
            var themeDialog = new AppearanceWindow(this); themeDialog.Show();
            var appearancePanel=(StackPanel)((ScrollViewer)themeDialog.Content).Content;
            var choices = appearancePanel.Children.OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="메신저 색상 테마");
            var awayChoice=appearancePanel.Children.OfType<ComboBox>().Single(c=>System.Windows.Automation.AutomationProperties.GetName(c)=="자동 자리비움 시간");awayChoice.SelectedIndex=2;
            if(JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(LocalDirectory,"preferences.json")))!.IdleMinutes!=10)throw new InvalidOperationException("자리비움 시간 저장 실패");awayChoice.SelectedIndex=1;
            var glass = appearancePanel.Children.OfType<System.Windows.Controls.CheckBox>().Single();
            foreach (var palette in Appearance.Palettes)
            {
                choices.SelectedIndex = Array.IndexOf(Appearance.Palettes, palette);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(LocalDirectory, "preferences.json")))!;
                if (preferences.ThemeId != palette.Id || ((SolidColorBrush)FindResource("Blue")).Color != (Color)ColorConverter.ConvertFromString(palette.Accent)) throw new InvalidOperationException("화면 색상 적용·저장 실패");
                if (((SolidColorBrush)themedInbox.ReceivedTab.Foreground).Color != ((SolidColorBrush)FindResource("Blue")).Color) throw new InvalidOperationException("열린 메시지 창의 테마 변경 실패");
                SavePreview(this, Path.Combine(directory, "theme-" + palette.Id + ".png"));
            }
            glass.IsChecked = false;
            if (JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(LocalDirectory, "preferences.json")))!.Glass || ((SolidColorBrush)FindResource("Glass")).Color.A != 255) throw new InvalidOperationException("유리 효과 해제·저장 실패");
            SavePreview(themeDialog, Path.Combine(directory, "appearance-preview.png")); themeDialog.Close(); themedInbox.Close();
            Appearance.Apply("unknown-theme", true);
            if (Appearance.Selected != "blue") throw new InvalidOperationException("잘못된 색상 설정 복구 실패");
            Appearance.Apply(originalTheme, originalGlass); RefreshAppearance(); SavePreferences();
            foreach (var state in new[] { false, true })
            {
                var menu = PersonMenu(people[0] with { Online = state,Away=false,Mobile=false });
                var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToArray();
                if (headers[0] != (state ? "온라인" : "오프라인") || headers.Any(h => h?.Contains("일정") == true || h?.Contains("알림톡") == true)) throw new InvalidOperationException("접속 상태별 메뉴 검증 실패");
                menu.PlacementTarget = OrganizationTree; menu.IsOpen = true;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                var border = menu.Items.Count > 0 ? (Visual)menu : menu;
                var menuBitmap = new RenderTargetBitmap((int)menu.ActualWidth, (int)menu.ActualHeight, 96, 96, PixelFormats.Pbgra32); menuBitmap.Render(border);
                var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
                using (var file = File.Create(Path.Combine(directory, state ? "online-menu.png" : "offline-menu.png"))) menuEncoder.Save(file);
                menu.IsOpen = false;
            }
            var inbox = OpenMailbox("received"); await inbox.Reload();
            var messages = await Api!.Get<MessageItem[]>("api/messages?box=received");
            if (messages.Length == 0) throw new InvalidOperationException("메시지 검증 데이터 없음");
            await inbox.ShowMessage(messages[0].Id); SavePreview(inbox, Path.Combine(directory, "mailbox-preview.png"));
            Archive.Verify(await Api.Get<Detail>("api/messages/"+messages[0].Id));
            ((System.Windows.Controls.Button)inbox.Template.FindName("CloseAction", inbox)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (var i = 0; i < 20 && inbox.IsVisible; i++) await Task.Delay(25);
            if (inbox.IsVisible) throw new InvalidOperationException("제목줄 닫기 버튼 실패");
            var compose = new ComposeWindow(this, people, null, "Windows 앱 전송 검증",people.Select(p=>p.Id).Take(2).ToArray()); compose.Show();
            compose.BodyBox.Text = "교사용 프로그램의 작성 창에서 실제로 전송한 검증 메시지입니다.";compose.VerifyEditor(); SavePreview(compose, Path.Combine(directory, "compose-preview.png"));
            compose.ScheduleSend.IsChecked=true;SavePreview(compose,Path.Combine(directory,"scheduled-compose-preview.png"));compose.ScheduleSend.IsChecked=false;
            compose.SendButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (var i = 0; i < 100 && compose.IsVisible; i++) await Task.Delay(100);
            if (compose.IsVisible) throw new InvalidOperationException("작성 창 전송 실패: " + compose.Feedback.Text);
            if (!(await Api.Get<MessageItem[]>("api/messages?box=sent")).Any(m => m.Title == "Windows 앱 전송 검증")) throw new InvalidOperationException("전송 기록 없음");
            var recallCheck=OpenMailbox("sent");await recallCheck.VerifyRecallState();recallCheck.Close();
            var registration = new RegistrationWindow(ServerAddress.Text) { Owner = this }; registration.Show();
            await registration.PolicyReady;
            foreach(var choice in registration.PolicyChoices.Values){choice.IsEnabled=true;choice.IsChecked=true;}
            registration.UsernameBox.Text = "native.teacher"; registration.NameBox.Text = "가입신청교사"; registration.DepartmentBox.Text = "교무기획부";
            registration.PasswordBox.Password = registration.ConfirmBox.Password = Environment.GetEnvironmentVariable("SCHOOL_UI_PASSWORD")!;
            registration.SubmitButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (var i = 0; i < 50 && !registration.SubmitButton.Content.ToString()!.StartsWith("신청 완료", StringComparison.Ordinal); i++) await Task.Delay(100);
            if (!registration.SubmitButton.Content.ToString()!.StartsWith("신청 완료", StringComparison.Ordinal)) throw new InvalidOperationException("Windows 계정 신청 실패: " + registration.Feedback.Text);
            SavePreview(registration, Path.Combine(directory, "registration-preview.png")); registration.Close();
            await VerifyFeatureUi(directory);
            await SignOut();
            if (savedLogin.Exists || RememberLogin.IsChecked == true) throw new InvalidOperationException("로그아웃 시 자동 로그인 삭제 실패");
            SavePreview(this, Path.Combine(directory, "login-preview.png"));
            exiting = true; System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception error) { Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/ui-error.txt", error.ToString()); exiting = true; System.Windows.Application.Current.Shutdown(1); }
    }
}
record Preferences(string ServerAddress, string Username, bool Quiet, string[] Favorites, bool DoNotDisturb = false, bool AutoLogin = false, string ThemeId = "blue", bool Glass = true,int IdleMinutes=5);
