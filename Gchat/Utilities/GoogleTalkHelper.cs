﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Coding4Fun.Phone.Controls;
using Gchat.Data;
using Gchat.Protocol;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Phone.Tasks;

namespace Gchat.Utilities {
    public class GoogleTalkHelper {
        public const int MaximumChatLogSize = 50;
        public const int RecentContactsCount = 10;

        #region Public Events

        public delegate void LoginCallback(string token);

        public delegate void ErrorCallback(string token);

        public delegate void MessageReceivedEventHandler(Message message);

        public event MessageReceivedEventHandler MessageReceived;

        public delegate void ConnectEventHandler();

        public event ConnectEventHandler Connect;

        public delegate void ConnectFailedEventHandler(string message, string title);

        public event ConnectFailedEventHandler ConnectFailed;

        public delegate void RosterUpdatedEventHandler();

        public event RosterUpdatedEventHandler RosterUpdated;

        public delegate void ImageDownloaded(BitmapImage image);

        #endregion

        #region Public Properties

        public bool Connected { get; private set; }

        public bool RosterLoaded { get; private set; }

        #endregion

        #region Private Fields

        private readonly IsolatedStorageSettings settings;
        private readonly GoogleTalk gtalk;
        private readonly PushHelper pushHelper;
        private bool hasToken;
        private bool hasUri;
        private string registeredUri;
        private bool offlineMessagesDownloaded;
        private static readonly Regex linkRegex = new Regex("(?:(\\B(?:;-?\\)|:-?\\)|:-?D|:-?P|:-?S|:-?/|:-?\\||:'\\(|:-?\\(|<3))|(https?://)?(([0-9]{1-3}\\.[0-9]{1-3}\\.[0-9]{1-3}\\.[0-9]{1-3})|(([a-z0-9-]+\\.)+[a-z]{2,4}))(/[-a-z0-9+&@#\\/%?=~_|!:,.;]*(?:\\([-a-z0-9+&@#\\/%?=~_|!:,.;()]*[-a-z0-9+@#\\/%=~_|)]|[-a-z0-9+@#\\/%=~_|]))?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private Queue<ToastPrompt> messageQueue = new Queue<ToastPrompt>();
        private bool messageShowing;
        private Dictionary<string, ManualResetEvent> photoLocks = new Dictionary<string, ManualResetEvent>();

        #endregion

        #region Public Methods

        public GoogleTalkHelper() {
            settings = App.Current.Settings;
            gtalk = App.Current.GtalkClient;
            pushHelper = App.Current.PushHelper;

            pushHelper.UriUpdated += UriUpdated;
            pushHelper.RawNotificationReceived += RawNotificationReceived;
            Connected = false;
        }

        public static bool IsPaid() {
#if PAID
            return !(new Microsoft.Phone.Marketplace.LicenseInformation()).IsTrial();
#else
            return false;
#endif
        }

        public void LoginIfNeeded() {
            if (!App.Current.Settings.Contains("auth")) {
                App.Current.RootFrame.Dispatcher.BeginInvoke(
                    () => App.Current.RootFrame.Navigate(new Uri("/Pages/Login.xaml", UriKind.Relative))
                );

                return;
            }

            if (gtalk.LoggedIn) {
                return;
            }

            Connected = false;

            if (settings.Contains("token") && settings.Contains("rootUrl")) {
                var tokenBytes = ProtectedData.Unprotect(settings["token"] as byte[], null);
                App.Current.GtalkClient.SetToken(Encoding.UTF8.GetString(tokenBytes, 0, tokenBytes.Length));
                App.Current.GtalkClient.RootUrl = settings["rootUrl"] as string;

                TokenUpdated();
            } else {
                var authBytes = ProtectedData.Unprotect(settings["auth"] as byte[], null);
                App.Current.GtalkClient.Login(
                    settings["username"] as string,
                    Encoding.UTF8.GetString(authBytes, 0, authBytes.Length),
                    token => {
                        settings["token"] = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null);
                        settings["rootUrl"] = App.Current.GtalkClient.RootUrl;

                        TokenUpdated();
                    },
                    error => {
                        if (error.Equals("")) {
                            if (ConnectFailed != null) {
                                ConnectFailed(
                                    AppResources.Error_ConnectionErrorMessage,
                                    AppResources.Error_ConnectionErrorTitle
                                );
                            }
                        } else if (error.StartsWith("401")) {
                            // stale auth token. get a new one and we should be all happy again.
                            settings.Remove("auth");

                            App.Current.RootFrame.Dispatcher.BeginInvoke(
                                () => {
                                    MessageBox.Show(
                                        AppResources.Error_AuthErrorMessage,
                                        AppResources.Error_AuthErrorTitle,
                                        MessageBoxButton.OK
                                    );
                                    App.Current.RootFrame.Navigate(new Uri("/Pages/Login.xaml", UriKind.Relative));
                                });
                        } else {
                            if (ConnectFailed != null) {
                                ConnectFailed(error, "Login");
                            }
                        }
                    }
                );
            }
        }

        public void Logout() {
            Connected = false;
            RosterLoaded = false;
            offlineMessagesDownloaded = false;

            settings.Clear();
            settings["chatlog"] = new Dictionary<string, List<Message>>();
            settings["unread"] = new Dictionary<string, int>();

            App.Current.RecentContacts.Clear();
            App.Current.Roster.Clear();

            hasToken = false;
            hasUri = false;
            registeredUri = null;

            App.Current.PushHelper.CloseChannel();
            App.Current.PushHelper.RegisterPushNotifications();

            if (gtalk.LoggedIn) {
                gtalk.Logout(data => { }, error => { });
            }

            App.Current.RootFrame.Dispatcher.BeginInvoke(
                () => App.Current.RootFrame.Navigate(new Uri("/Pages/Login.xaml", UriKind.Relative))
            );
        }

        public void ShowToast(Message m) {
            if (!m.Offline && !string.IsNullOrEmpty(m.Body)) {
                App.Current.RootFrame.Dispatcher.BeginInvoke(() => {
                    Contact c = App.Current.Roster[m.From];
                    var t = new ToastPrompt {    
                        Title = c != null ? c.NameOrEmail : m.From,
                        Message = m.Body,
                        ImageSource = new BitmapImage(new Uri("/ToastIcon.png", UriKind.RelativeOrAbsolute))
                    };

                    t.Completed += (s, ev) => {
                        if (ev.PopUpResult == PopUpResult.Ok) {
                            App.Current.RootFrame.Navigate(new Uri("/Pages/Chat.xaml?from=" + m.From, UriKind.Relative));
                        }

                        lock(messageQueue) {
                            if(messageQueue.Count == 0) {
                                messageShowing = false;
                            } else {
                                messageQueue.Dequeue().Show();
                            }
                        }
                    };

                    messageQueue.Enqueue(t);

                    lock(messageQueue) {
                        if(!messageShowing) {
                            messageShowing = true;
                            messageQueue.Dequeue().Show();
                        }
                    }
                });
            }
        }

        public void ShowToast(string message) {
            ShowToast(message, null);
        }

        public void ShowToast(string message, string title) {
            App.Current.RootFrame.Dispatcher.BeginInvoke(() => {
                var toast = new ToastPrompt {
                    Title = title ?? "",
                    Message = message ?? "",
                    Background = (Brush)Application.Current.Resources["PhoneChromeBrush"],
                    Foreground = (Brush)Application.Current.Resources["PhoneForegroundBrush"]
                };

                toast.Completed += (s, ev) => {
                    if (ev.PopUpResult == PopUpResult.Ok) {
                        App.Current.RootFrame.Dispatcher.BeginInvoke(() => MessageBox.Show(message ?? "", title ?? "", MessageBoxButton.OK));
                    }

                    lock (messageQueue) {
                        if (messageQueue.Count == 0) {
                            messageShowing = false;
                        } else {
                            messageQueue.Dequeue().Show();
                        }
                    }
                };

                messageQueue.Enqueue(toast);

                lock (messageQueue) {
                    if (!messageShowing) {
                        messageShowing = true;
                        messageQueue.Dequeue().Show();
                    }
                }
            });
        }

        public void UriUpdated(string uri) {
            hasUri = true;

            lock (this) {
                if (hasUri && hasToken) {
                    if (!uri.Equals(registeredUri)) {
                        registeredUri = uri;

                        Register(registeredUri);
                    }
                }
            }
        }

        public void TokenUpdated() {
            hasToken = true;

            lock (this) {
                if (hasUri && hasToken) {
                    if (!pushHelper.PushChannelUri.Equals(registeredUri)) {
                        registeredUri = pushHelper.PushChannelUri;

                        Register(registeredUri);
                    }
                }
            }
        }

        public void RawNotificationReceived(string data) {
            if (data.StartsWith("msg:")) {
                gtalk.ParseMessage(
                    data.Substring(4),
                    NotifyMessageReceived,
                    error => ShowToast(error, "Message parsing")
                );
            }
        }

        public List<Message> ChatLog(string username) {
            if(username.Contains("/")) {
                username = username.Substring(0, username.IndexOf('/'));
            }

            var chatLog = settings["chatlog"] as Dictionary<string, List<Message>>;

            lock (chatLog) {
                if (!chatLog.ContainsKey(username)) {
                    chatLog.Add(username, new List<Message>());
                }

                return chatLog[username];
            }
        }

        public void DownloadImage(Contact contact, Action finished, Action error) {
            var fileName = "Shared/ShellContent/" + contact.PhotoHash + ".jpg";

            System.Diagnostics.Debug.WriteLine("Downloading " + fileName + " for " + contact);

            using (IsolatedStorageFile isf = IsolatedStorageFile.GetUserStoreForApplication()) {
                if (isf.FileExists(fileName)) {
                    finished();
                    return;
                }

                var fileStream = isf.CreateFile(fileName);

                var req = WebRequest.CreateHttp(gtalk.RootUrl + "/images/" + contact.PhotoHash);

                req.BeginGetResponse(a => {
                    HttpWebResponse response;

                    try {
                        response = (HttpWebResponse)req.EndGetResponse(a);
                    } catch (Exception e) {
                        System.Diagnostics.Debug.WriteLine(e);
                        error();
                        return;
                    }

                    using (var responseStream = response.GetResponseStream()) {
                        var data = new byte[response.ContentLength];

                        responseStream.BeginRead(
                            data,
                            0,
                            data.Length,
                            result =>
                                fileStream.BeginWrite(
                                    data,
                                    0,
                                    data.Length,
                                    async => {
                                        fileStream.Close();

                                        System.Diagnostics.Debug.WriteLine("Finished downloading " + fileName + " for " + contact);
                                        finished();
                                    },
                                    null
                                )
                            ,
                            null
                        );
                    }
                }, null);
            }
        }

        public static Paragraph Linkify(string message) {
            var paragraph = new Paragraph();

            int last = 0;

            List<Uri> imgurUris = new List<Uri>();

            foreach (Match m in linkRegex.Matches(message)) {
                if (m.Index > last) {
                    AddWithFormat(message.Substring(last, m.Index-last), paragraph);
                }

                if (m.Groups[1].Value != string.Empty) {
                    var smiley = m.Groups[0].Value.ToUpperInvariant();
                    string smileyName = "smile.8";

                    switch (smiley) {
                        case ":)":
                        case ":-)":
                            smileyName = "smile.1";
                            break;
                        case ";)":
                        case ";-)":
                            smileyName = "smile.15"; // awkward drunken smile
                            break;
                        case ":D":
                        case ":-D":
                            smileyName = "smile.10";
                            break;
                        case ":P":
                        case ":-P":
                            smileyName = "smile.14";
                            break;
                        case ":S":
                        case ":-S":
                            smileyName = "smile.20";
                            break;
                        case ":/":
                        case ":-/":
                            smileyName = "smile.17";
                            break;
                        case ":|":
                        case ":-|":
                            smileyName = "smile.7";
                            break;
                        case ":'(":
                            smileyName = "smile.22";
                            break;
                        case ":(":
                        case ":-(":
                            smileyName = "smile.18";
                            break;
                        case"<3":
                            smileyName = "heart";
                            break;
                    }

                    paragraph.Inlines.Add(
                            new InlineUIContainer {
                                Child = new Image {
                                    Source = new BitmapImage(new Uri("/icons/appbar." + smileyName + ".rest.png", UriKind.Relative)),
                                    MaxWidth = 48,
                                    MaxHeight = 48,
                                    Margin = new Thickness(-12)
                                }
                            }
                        );
                } else {
                    string uri = m.Groups[0].Value;

                    if (uri.StartsWith("ra.ge/", StringComparison.InvariantCultureIgnoreCase) &&
                        App.Current.Settings.Contains("rages") && (bool) App.Current.Settings["rages"]) {
                        var rageUri = new Uri(
                            "/icons/emoticon.rage." + uri.Substring(6).Replace("!", "_") + ".png", UriKind.Relative);
                        paragraph.Inlines.Add(
                            new InlineUIContainer {
                                Child = new Image {
                                    Source = new BitmapImage(rageUri),
                                    MaxWidth = 48,
                                    MaxHeight = 48,
                                    Stretch = Stretch.None
                                }
                            }
                        );
                    } else {
                        if (!uri.StartsWith("http://") && !uri.StartsWith("https://")) {
                            uri = uri.Insert(0, "http://");
                        }

                        // TODO: Investigate why this crashes instead of just ignoring the error.
                        try {
                            var link = new Hyperlink {
                                NavigateUri = new Uri(uri),
                                TargetName = "_blank"
                            };
                            link.Inlines.Add(m.Groups[0].Value);

                            paragraph.Inlines.Add(link);

                            if (uri.StartsWith("http://i.imgur.com/") && uri.EndsWith(".jpg")) {
                                imgurUris.Add(link.NavigateUri);
                            }
                        } catch (Exception) {
                            paragraph.Inlines.Add(
                                new Run {
                                    Text = uri
                                }
                            );
                        }
                    }
                }

                last = m.Index + m.Length;
            }

            if (last != message.Length) {
                AddWithFormat(message.Substring(last), paragraph);
            }

            if (imgurUris.Count > 0 && (!App.Current.Settings.Contains("imgur") || (bool)App.Current.Settings["imgur"] != false)) {
                foreach (var uri in imgurUris) {
                    var uriString = uri.AbsoluteUri;

                    if (uriString.Length == 28) {
                        uriString = uriString.Substring(0, 24) + "s.jpg";
                    }

                    var img = new Image {
                        Source = new BitmapImage {
                            UriSource = new Uri(
                                uriString,
                                UriKind.Absolute
                            )
                        },
                        Margin = new Thickness(0, 5, 0, 0)
                    };

                    img.Tap += (s, r) => {
                        try {
                            var t = new WebBrowserTask {
                                Uri = uri
                            };
                            t.Show();
                        } catch (InvalidOperationException) {
                            // why, Windows Phone? why do you make me add pointless try/catches?
                        }
                    };

                    paragraph.Inlines.Add(new InlineUIContainer {
                        Child = img
                    });
                }
            }

            return paragraph;
        }

        private static void AddWithFormat(string text, Paragraph paragraph) {
            int italics = -1;
            int bold = -1;
            int pos = 0;

            for (var i = 0; i < text.Length; i++) {
                if (text[i] == '_') {
                    FormatHelper(text, paragraph, i, italics, bold, ref pos, '_', ref italics);
                } else if (text[i] == '*') {
                    FormatHelper(text, paragraph, i, italics, bold, ref pos, '*', ref bold);
                }
            }

            if (pos <= text.Length - 1) {
                paragraph.Inlines.Add(
                    new Run {
                        Text = text.Substring(pos)
                    }
                );
            }
        }

        private static void FormatHelper(string text, Paragraph paragraph, int i, int italics, int bold, ref int pos, char character, ref int checking) {
            string open = " *_-";
            string close = " .,!*_-";

            if (checking == -1 && (i == 0 || open.IndexOf(text[i - 1]) != -1)) {
                // good candidate for format-start!

                for (var j = i + 2; j < text.Length; j++) {
                    if (text[j] == character && (j == text.Length - 1 || close.IndexOf(text[j + 1]) != -1)) {
                        // i -> j is format.

                        paragraph.Inlines.Add(
                            new Run {
                                Text = text.Substring(pos, i - pos),
                                FontStyle = italics == -1 ? FontStyles.Normal : FontStyles.Italic,
                                FontWeight = bold == -1 ? FontWeights.Normal : FontWeights.Bold
                            }
                        );

                        pos = i + 1;
                        checking = j;
                        break;
                    }
                }
            } else if (i == checking) {
                // end of format. flush the buffer.

                paragraph.Inlines.Add(
                    new Run {
                        Text = text.Substring(pos, i - pos),
                        FontStyle = italics == -1 ? FontStyles.Normal : FontStyles.Italic,
                        FontWeight = bold == -1 ? FontWeights.Normal : FontWeights.Bold
                    }
                );

                checking = -1;
                pos = i + 1;
            }
        }

        public static void GoogleLogin(string username, string password, LoginCallback callback, ErrorCallback error) {
            var data = Encoding.UTF8.GetBytes(
                "accountType=HOSTED_OR_GOOGLE" +
                "&Email=" + HttpUtility.UrlEncode(username) +
                "&Passwd=" + HttpUtility.UrlEncode(password) +
                "&service=mail" +
                "&source=gchatapp.com-gchat-" + AppResources.AppVersion
            );

            var req = WebRequest.CreateHttp("https://www.google.com/accounts/ClientLogin");

            req.ContentType = "application/x-www-form-urlencoded";
            req.Method = "POST";
            req.AllowReadStreamBuffering = true;
            req.Headers["Content-Length"] = data.Length.ToString();

            req.BeginGetRequestStream(
                ar => {
                    using (var requestStream = req.EndGetRequestStream(ar)) {
                        requestStream.Write(data, 0, data.Length);
                    }

                    req.BeginGetResponse(
                        a => {
                            try {
                                var response = req.EndGetResponse(a) as HttpWebResponse;

                                var responseStream = response.GetResponseStream();
                                using (var sr = new StreamReader(responseStream)) {
                                    string line;

                                    while ((line = sr.ReadLine()) != null && !line.StartsWith("Auth=")) {
                                    }

                                    callback(line.Split(new[] { '=' })[1]);
                                }
                            } catch (WebException e) {
                                var response = e.Response as HttpWebResponse;

                                try {
                                    using (var responseStream = response.GetResponseStream()) {
                                        using (var sr = new StreamReader(responseStream)) {
                                            if(error != null) {
                                                string errorString = sr.ReadToEnd();

                                                if (errorString.StartsWith("Error=BadAuth")) {
                                                    error(AppResources.Error_AuthError);
                                                } else {
                                                    error(AppResources.Error_ConnectionErrorMessage);
                                                }
                                            }
                                        }
                                    }
                                } catch (Exception) {
                                    // What is wrong with this platform?!
                                    if(error != null) {
                                        error(AppResources.Error_ConnectionErrorMessage);
                                    }
                                }
                            }
                        },
                        null
                    );
                },
                null
            );
        }

        public void GetOfflineMessages(GoogleTalk.FinishedCallback cb) {
            Dictionary<string, string> firstMessage = new Dictionary<string, string>();
            Dictionary<string, int> messageCount = new Dictionary<string, int>();

            gtalk.MessageQueue(
                message => {
                    message.Offline = true;
                    NotifyMessageReceived(message);

                    var email = message.From;
                    if (email.Contains("/")) {
                        email = email.Substring(0, email.IndexOf('/'));
                    }

                    if (!messageCount.ContainsKey(email)) {
                        messageCount[email] = 1;
                        firstMessage[email] = message.Body;
                    } else {
                        messageCount[email]++;
                    }
                },
                error => {
                    if (error.Equals("")) {
                        ShowToast(AppResources.Error_OfflineMessagesMessage);
                    } else if (error.StartsWith("403")) {
                        GracefulReLogin();
                    } else {
                        ShowToast(error, AppResources.Error_OfflineMessagesTitle);
                    }
                    cb();
                },
                () => {
                    foreach(var mc in messageCount) {
                        if (mc.Value == 1) {
                            if (mc.Key != App.Current.CurrentChat) {
                                ShowToast(new Message {
                                    From = mc.Key,
                                    Body = firstMessage[mc.Key]
                                });
                            }
                        } else {
                            if (mc.Key != App.Current.CurrentChat) {
                                ShowToast(new Message {
                                    From = mc.Key,
                                    Body = string.Format(AppResources.Notification_OfflineMessages, mc.Value)
                                });
                            }
                        }
                    }
                    cb();
                }
            );

            App.Current.RootFrame.Dispatcher.BeginInvoke(() => {
                var tileToFind = ShellTile.ActiveTiles.First();
                var newTileData = new StandardTileData {
                    Count = 0
                };
                tileToFind.Update(newTileData);
            });
        }

        public void LoadRoster() {
            gtalk.GetRoster(
                roster => App.Current.RootFrame.Dispatcher.BeginInvoke(
                    () => {
                        var unread = settings["unread"] as Dictionary<string, int>;

                        App.Current.Roster.Notify = false;

                        foreach (var contact in roster) {
                            if (App.Current.Roster.Contains(contact.Email)) {
                                var original =
                                    App.Current.Roster[contact.Email];

                                original.Name = contact.Name ??
                                                original.Name;
                                original.PhotoHash = contact.PhotoHash ??
                                                 original.PhotoHash;

                                original.SetSessions(contact.Sessions);
                            } else {
                                if (unread.ContainsKey(contact.Email)) {
                                    contact.UnreadCount = unread[contact.Email];
                                }

                                App.Current.Roster.Add(contact);
                            }
                        }
                        App.Current.Roster.Notify = true;

                        RosterLoaded = true;
                        if (RosterUpdated != null) {
                            RosterUpdated();
                        }

                        if (!offlineMessagesDownloaded) {
                            offlineMessagesDownloaded = true;
                            GetOfflineMessages(() => { });
                        }
                    }
                ),
                error => {
                    if (error.Equals("")) {
                        if (ConnectFailed != null) {
                            ConnectFailed(AppResources.Error_ContactListMessage, AppResources.Error_ContactListTitle);
                        }
                    } else if (error.StartsWith("403")) {
                        GracefulReLogin();
                    } else {
                        if (ConnectFailed != null) {
                            ConnectFailed(error, AppResources.Error_ContactListTitle);
                        }
                    }
                }
            );
        }

        public void SetCorrectOrientation(PhoneApplicationPage page) {
            if (App.Current.Settings.Contains("rotate") && (bool)App.Current.Settings["rotate"]) {
                page.SupportedOrientations = SupportedPageOrientation.Portrait;
            } else {
                page.SupportedOrientations = SupportedPageOrientation.PortraitOrLandscape;
            }
        }

        public Uri GetPinUri(string email) {
            return new Uri("/Pages/Chat.xaml?from=" + HttpUtility.UrlEncode(email), UriKind.Relative);
        }

        public bool IsContactPinned(string email) {
            Uri url = GetPinUri(email);
            ShellTile existing = ShellTile.ActiveTiles.FirstOrDefault(x => x.NavigationUri == url);

            return existing != null;
        }

        public void PinContact(string email) {
            if (!IsContactPinned(email)) {
                FlurryWP7SDK.Api.LogEvent("Contact pinned");

                Contact contact = App.Current.Roster[email];
                StandardTileData tile;

                if (contact != null) {
                    tile = new StandardTileData {
                        Title = contact.NameOrEmail
                    };

                    if (contact.PhotoHash != null) {
                        DownloadImage(
                            contact,
                            () => {
                                tile.BackgroundImage =
                                    new Uri("isostore:/Shared/ShellContent/" + contact.PhotoHash + ".jpg");

                                ShellTile.Create(GetPinUri(email), tile);
                            },
                            () => {
                                ShellTile.Create(GetPinUri(email), tile);
                            }
                        );
                    } else {
                        ShellTile.Create(GetPinUri(email), tile);
                    }
                } else {
                    tile = new StandardTileData {
                        Title = email
                    };

                    ShellTile.Create(GetPinUri(email), tile);
                }
            }
        }

        public void AddRecentContact(Contact contact) {
            var found = false;

            if (contact == null || App.Current.RecentContacts == null) {
                // Sorry, I'd rather have it do a harmless but wrong operation than crashing.
                return;
            }

            for (var i = 0; i < App.Current.RecentContacts.Count; i++) {
                if (App.Current.RecentContacts[i] == null) {
                    App.Current.RecentContacts.RemoveAt(i);
                    i--;
                } else if (App.Current.RecentContacts[i].Email == contact.Email) {
                    App.Current.RecentContacts.RemoveAt(i);
                    found = true;
                    break;
                }
            }

            if (!found && App.Current.RecentContacts.Count == RecentContactsCount) {
                App.Current.RecentContacts.RemoveAt(RecentContactsCount - 1);
            }

            App.Current.RecentContacts.Insert(0, contact);
        }

        #endregion

        #region Private Methods

        private void Register(string uri) {
            if (!settings.Contains("clientkey")) {
                gtalk.GetKey(
                    clientKey => {
                        settings["clientkey"] = ProtectedData.Protect(Encoding.UTF8.GetBytes(clientKey), null);

                        Register(uri, true);
                    },
                    error => {
                        if (error.Equals("")) {
                            if (ConnectFailed != null) {
                                ConnectFailed(
                                    AppResources.Error_ConnectionErrorMessage,
                                    AppResources.Error_ConnectionErrorTitle
                                );
                            }
                        } else if (error.StartsWith("403")) {
                            GracefulReLogin();
                        } else {
                            if (ConnectFailed != null) {
                                ConnectFailed(error, AppResources.Error_RegisterTitle);
                            }
                        }
                    }
                );
            } else {
                Register(uri, false);
            }
        }

        private void Register(string uri, bool keySet) {
            if (!keySet) {
                var clientKeyBytes = ProtectedData.Unprotect(settings["clientkey"] as byte[], null);
                gtalk.SetKey(Encoding.UTF8.GetString(clientKeyBytes, 0, clientKeyBytes.Length));
            }

            var secondaryTilePrefix = "/Pages/Chat.xaml?from=";

            var tiles = ShellTile.ActiveTiles
                .Where(tile => tile.NavigationUri.OriginalString.StartsWith(secondaryTilePrefix))
                .Select(tile => HttpUtility.UrlDecode(tile.NavigationUri.OriginalString.Substring(secondaryTilePrefix.Length)));

            gtalk.Register(
                uri,
                tiles,
                data => {
                    LoadRoster();

                    Connected = true;
                    if (Connect != null) {
                        Connect();
                    }
                },
                error => {
                    if (error.Equals("")) {
                        if (ConnectFailed != null) {
                            ConnectFailed(
                                AppResources.Error_ConnectionErrorMessage,
                                AppResources.Error_ConnectionErrorTitle
                            );
                        }
                    } else if (error.StartsWith("403")) {
                        GracefulReLogin();
                    } else if (error.StartsWith("401")) {
                        ConnectFailed(
                            AppResources.Error_ApiMessage,
                            AppResources.Error_ApiTitle
                        );
                    } else {
                        if (ConnectFailed != null) {
                            ConnectFailed(error, AppResources.Error_RegisterTitle);
                        }
                    }
                }
            );
        }

        private void NotifyMessageReceived(Message message) {
            if (message.Body != null) {
                List<Message> chatLog = ChatLog(message.From);

                lock (chatLog) {
                    while (chatLog.Count >= MaximumChatLogSize) {
                        chatLog.RemoveAt(0);
                    }
                    chatLog.Add(message);
                }

                var email = message.From;

                if (email.Contains("/")) {
                    email = email.Substring(0, email.IndexOf('/'));
                }

                var contact = App.Current.Roster[email];

                if (contact == null) {
                    // TODO: only for sanity-of-mind-purposes. MUST remove eventually
                    return;
                }

                App.Current.RootFrame.Dispatcher.BeginInvoke(() => {
                    AddRecentContact(contact);
                });

                if (App.Current.CurrentChat == null || message.From.IndexOf(App.Current.CurrentChat) != 0) {
                    var unread = settings["unread"] as Dictionary<string, int>;

                    int unreadCount = 1;

                    lock (unread) {
                        if (!unread.ContainsKey(email)) {
                            unread.Add(email, 1);
                        } else {
                            unreadCount = ++unread[email];
                        }
                    }

                    if (contact != null) {
                        App.Current.RootFrame.Dispatcher.BeginInvoke(() => contact.UnreadCount = unreadCount);
                    }
                }
            }

            if (MessageReceived != null) {
                MessageReceived(message);
            }
        }

        private void GracefulReLogin() {
            settings.Remove("token");
            settings.Remove("rootUrl");

            gtalk.SetToken(null);
            gtalk.RootUrl = GoogleTalk.DefaultRootUrl;

            hasToken = false;
            registeredUri = null;

            LoginIfNeeded();
        }

        #endregion
    }
}
