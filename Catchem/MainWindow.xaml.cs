﻿using GMap.NET;
using GMap.NET.WindowsPresentation;
using PoGo.PokeMobBot.Logic;
using PoGo.PokeMobBot.Logic.Common;
using PoGo.PokeMobBot.Logic.Event;
using PoGo.PokeMobBot.Logic.Logging;
using PoGo.PokeMobBot.Logic.State;
using PoGo.PokeMobBot.Logic.Tasks;
using PoGo.PokeMobBot.Logic.Utils;
using POGOProtos.Enums;
using POGOProtos.Map.Fort;
using POGOProtos.Map.Pokemon;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Extensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using POGOProtos.Data;
using POGOProtos.Inventory.Item;
using static System.String;
using LogLevel = PoGo.PokeMobBot.Logic.Logging.LogLevel;

namespace Catchem
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        public static MainWindow BotWindow;
        private bool _windowClosing;
        string subPath = "Profiles";

        private readonly Dictionary<ISession, BotWindowData> _openedSessions = new Dictionary<ISession, BotWindowData>(); //may be not session... but some uniq obj for every running bot
        private ISession _curSession;

        private BotWindowData Bot
        {
            get
            {
                if (_curSession != null)
                {
                    if (_openedSessions.ContainsKey(_curSession))
                    {
                        return _openedSessions[_curSession];
                    }
                }
                return null;
            }
        }
        GMapMarker _playerMarker;

        bool _loadingUi;

        public MainWindow()
        {
            InitializeComponent();
            InitWindowsComtrolls();
            InitializeMap();
            BotWindow = this;

            LogWorker();
            MarkersWorker();
            MovePlayer();
            InitBots();
        }

        void InitWindowsComtrolls()
        {
            authBox.ItemsSource = Enum.GetValues(typeof(AuthType));
        }

        private async void InitializeMap()
        {
            pokeMap.Bearing = 0;

            pokeMap.CanDragMap = true;

            pokeMap.DragButton = MouseButton.Left;

            //pokeMap.GrayScleMode = true;

            //pokeMap.MarkersEnabled = true;

            pokeMap.MaxZoom = 18;

            pokeMap.MinZoom = 2;

            pokeMap.MouseWheelZoomType = MouseWheelZoomType.MousePositionWithoutCenter;

            //pokeMap.NegativeMode = false;

            //pokeMap.PolygonsEnabled = true;

            pokeMap.ShowCenter = false;

            //pokeMap.RoutesEnabled = true;

            pokeMap.ShowTileGridLines = false;

            pokeMap.Zoom = 18;

            pokeMap.MapProvider = GMap.NET.MapProviders.GMapProviders.GoogleMap;
            GMaps.Instance.Mode = AccessMode.ServerOnly;

            GMap.NET.MapProviders.GMapProvider.WebProxy = System.Net.WebRequest.GetSystemWebProxy();
            GMap.NET.MapProviders.GMapProvider.WebProxy.Credentials = System.Net.CredentialCache.DefaultCredentials;

            if (Bot != null)
                pokeMap.Position = new PointLatLng(Bot.Lat, Bot.Lng);

            await Task.Delay(10);
        }

        internal void InitBots()
        {
            Logger.SetLogger(new WpfLogger(LogLevel.Info), subPath);

            foreach (var item in Directory.GetDirectories(subPath))
            {
                if (item != subPath + "\\Logs")
                {
                    InitBot(GlobalSettings.Load(item), System.IO.Path.GetFileName(item));
                }
            }
        }


        public void ReceiveMsg(string msgType, ISession session, params object[] objData)
        {
            if (session == null) return;
            switch (msgType)
            {
                case "log":
                    PushNewConsoleRow(session, (string)objData[0], (Color)objData[1]);
                    break;
                case "ps":
                    PushNewPokestop(session, (IEnumerable<FortData>)objData[0]);
                    break;
                case "pm":
                    PushNewPokemons(session, (IEnumerable<MapPokemon>)objData[0]);
                    break;
                case "pmw":
                    PushNewWildPokemons(session, (IEnumerable<WildPokemon>)objData[0]);
                    break;
                case "pm_rm":
                    PushRemovePokemon(session, (MapPokemon)objData[0]);
                    break;
                case "p_loc":
                    UpdateCoords(session, objData);
                    break;
                case "pm_list":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        BuildPokemonList(session, objData);
                    }));
                    break;
                case "item_list":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        BuildItemList(session, objData);
                    }));
                    break;
                case "item_new":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        GotNewItems(session, objData);
                    }));
                    break;
                case "item_rem":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        LostItem(session, objData);
                    }));
                    break;
                case "pm_new":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        GotNewPokemon(session, objData);
                    }));
                    break;
                case "pm_rem":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        LostPokemon(session, objData);
                    }));
                    break;
                case "profile_data":
                    Dispatcher.BeginInvoke(new ThreadStart(delegate
                    {
                        UpdateProfileInfo(session, objData);
                    }));
                    break;
                case "forcemove_done":
                    PushRemoveForceMoveMarker(session);
                    break;
            }
        }

        private void LostItem(ISession session, object[] objData)
        {
            var receiverBot = _openedSessions[session];
            var lostAmount = (int) objData[1];
            var targetItem = receiverBot.ItemList.FirstOrDefault(x => x.Id == (ItemId)objData[0]);
            if (targetItem == null) return;
            if (targetItem.Amount <= lostAmount)
                receiverBot.ItemList.Remove(targetItem);
            else
                targetItem.Amount -= lostAmount;
            UpdateItemCollection(session);
        }

        private void GotNewItems(ISession session, object[] objData)
        {
            try
            {
                var newItems = (List<Tuple<ItemId, int>>)objData[0];
                var receiverBot = _openedSessions[session];

                foreach (var item in newItems)
                {
                    var targetItem = receiverBot.ItemList.FirstOrDefault(x => x.Id == item.Item1);
                    if (targetItem != null)
                        targetItem.Amount += item.Item2;
                    else
                        receiverBot.ItemList.Add(new ItemUiData(
                            item.Item1, 
                            item.Item1.ToInventorySource(), 
                            item.Item1.ToInventoryName(), 
                            item.Item2));
                }
                UpdateItemCollection(session);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void UpdateProfileInfo(ISession session, object[] objData)
        {
            Playername.Content = (string)objData[0];
            var targetBot = _openedSessions[session];
            targetBot.MaxItemStorageSize = (int) objData[1];
            targetBot.MaxPokemonStorageSize = (int)objData[2];
            l_coins.Content = (int)objData[3];
        }

        private void LostPokemon(ISession session, object[] objData)
        {
            try
            {
                var receiverBot = _openedSessions[session];
                var targetPokemon = receiverBot.PokemonList.FirstOrDefault(x => x.Id == (ulong) objData[0]);
                if (targetPokemon == null) return;
                receiverBot.PokemonList.Remove(targetPokemon);
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void GotNewPokemon(ISession session, object[] objData)
        {
            try
            {
                if ((ulong) objData[0] == 0) return;
                var receiverBot = _openedSessions[session];
                var pokemonId = (PokemonId) objData[1];
                receiverBot.PokemonList.Add(new PokemonUiData((ulong) objData[0], pokemonId.ToInventorySource(),
                    pokemonId.ToString(), (int) objData[2], (double) objData[3]));
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void BuildPokemonList(ISession session, object[] objData)
        {
            try
            {
                var receiverBot = _openedSessions[session];
                receiverBot.PokemonList = new ObservableCollection<PokemonUiData>();
                receiverBot.PokemonList.CollectionChanged += delegate
                {
                    UpdatePokemonCollection(session);
                };
                ((List<Tuple<PokemonData, double, int>>) objData[0]).ForEach(x => receiverBot.PokemonList.Add(
                    new PokemonUiData(x.Item1.Id,
                        x.Item1.PokemonId.ToInventorySource(),
                        (x.Item1.Nickname == "" ? x.Item1.PokemonId.ToString() : x.Item1.Nickname),
                        x.Item1.Cp, 
                        x.Item2)));
                if (session != _curSession) return;

                PokeListBox.ItemsSource = Bot.PokemonList;
            }
            catch (Exception)
            {
                // ignored
            }
        }
        private void BuildItemList(ISession session, object[] objData)
        {
            try
            {
                var receiverBot = _openedSessions[session];
                receiverBot.ItemList = new ObservableCollection<ItemUiData>();
                receiverBot.ItemList.CollectionChanged += delegate
                {
                    UpdateItemCollection(session);
                };
                ((List<ItemData>) objData[0]).ForEach(x => receiverBot.ItemList.Add(
                    new ItemUiData(x.ItemId,
                        x.ItemId.ToInventorySource(),
                        x.ItemId.ToInventoryName(),
                        x.Count)));
                if (session != _curSession) return;

                ItemListBox.ItemsSource = Bot.ItemList;
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void UpdateItemCollection(ISession session)
        {
            if (Bot == null || session != _curSession) return;
            l_inventory.Content = $"({Bot.ItemList.Sum(x=>x.Amount)}/{Bot.MaxItemStorageSize})";
        }

        private void UpdateCoords(ISession session, object[] objData)
        {
            try
            {
                if (session != _curSession)
                {
                    if (!_openedSessions.ContainsKey(session)) return;
                    var botReceiver = _openedSessions[session];
                    botReceiver.Lat = botReceiver._lat = (double)objData[0];
                    botReceiver.Lng = botReceiver._lng = (double)objData[1];
                }
                else
                {
                    Bot.MoveRequired = true;
                    if (Math.Abs(Bot._lat) < 0.001 && Math.Abs(Bot._lng) < 0.001)
                    {
                        Bot.Lat = Bot._lat = (double)objData[0];
                        Bot.Lng = Bot._lng = (double)objData[1];
                        Dispatcher.BeginInvoke(new ThreadStart(delegate
                        {
                            pokeMap.Position = new PointLatLng(Bot.Lat, Bot.Lng);
                        }));                        
                    }
                    else
                    {
                        Bot.Lat = (double)objData[0];
                        Bot.Lng = (double)objData[1];
                    }

                    if (_playerMarker == null)
                    {
                        Dispatcher.BeginInvoke(new ThreadStart(DrawPlayerMarker));                        
                    }
                    else
                    {
                        Bot.GotNewCoord = true;
                    }                    
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void DrawPlayerMarker()
        {
            _playerMarker = new GMapMarker(new PointLatLng(Bot.Lat, Bot.Lng))
            {
                Shape = Properties.Resources.trainer.ToImage("Player"),
                Offset = new Point(-14, -40),
                ZIndex = 15
            };
            pokeMap.Markers.Add(_playerMarker);

            if (Bot.ForceMoveMarker != null)
                pokeMap.Markers.Add(Bot.ForceMoveMarker);
        }

        #region DataFlow - Push

        private void PushNewConsoleRow(ISession session, string rowText, Color rowColor)
        {
            if (_openedSessions.ContainsKey(session))
            {
                _openedSessions[session].LogQueue.Enqueue(Tuple.Create(rowText, rowColor));
            }
        }

        private void PushRemoveForceMoveMarker(ISession session)
        {
            if (!_openedSessions.ContainsKey(session)) return;
            var tBot = _openedSessions[session];
            var nMapObj = new NewMapObject("forcemove_done", "", 0, 0, "");
            tBot.MarkersQueue.Enqueue(nMapObj);
        }

        private void PushRemovePokemon(ISession session, MapPokemon mapPokemon)
        {
            if (!_openedSessions.ContainsKey(session)) return;
            var tBot = _openedSessions[session];
            var nMapObj = new NewMapObject("pm_rm", mapPokemon.PokemonId.ToString(), mapPokemon.Latitude, mapPokemon.Longitude, mapPokemon.EncounterId.ToString());
            tBot.MarkersQueue.Enqueue(nMapObj);
        }

        private void PushNewPokemons(ISession session, IEnumerable<MapPokemon> pokemons)
        {
            if (!_openedSessions.ContainsKey(session)) return;
            foreach (var pokemon in pokemons)
            {
                var tBot = _openedSessions[session];
                if (tBot.MapMarkers.ContainsKey(pokemon.EncounterId.ToString()) ||
                    tBot.MarkersQueue.Count(x => x.Uid == pokemon.EncounterId.ToString()) != 0) continue;
                var nMapObj = new NewMapObject("pm", pokemon.PokemonId.ToString(), pokemon.Latitude, pokemon.Longitude, pokemon.EncounterId.ToString());
                tBot.MarkersQueue.Enqueue(nMapObj);
            }
        }

        private void PushNewWildPokemons(ISession session, IEnumerable<WildPokemon> pokemons)
        {
            if (!_openedSessions.ContainsKey(session)) return;
            foreach (var pokemon in pokemons)
            {
                var tBot = _openedSessions[session];
                if (tBot.MapMarkers.ContainsKey(pokemon.EncounterId.ToString()) ||
                    tBot.MarkersQueue.Count(x => x.Uid == pokemon.EncounterId.ToString()) != 0) continue;
                var nMapObj = new NewMapObject("pm", pokemon.PokemonData.PokemonId.ToString(), pokemon.Latitude, pokemon.Longitude, pokemon.EncounterId.ToString());
                tBot.MarkersQueue.Enqueue(nMapObj);
            }
        }

        private void PushNewPokestop(ISession session, IEnumerable<FortData> pstops)
        {
            if (!_openedSessions.ContainsKey(session)) return;
            var fortDatas = pstops as FortData[] ?? pstops.ToArray();
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < fortDatas.Length; i++)
            {
                try
                {
                    var tBot = _openedSessions[session];
                    try
                    {
                        if (tBot.MapMarkers.ContainsKey(fortDatas[i].Id) || tBot.MarkersQueue.Count(x => x.Uid == fortDatas[i].Id) != 0)
                        continue;
                    }
                    catch (Exception )//ex)
                    {
                        // ignored
                    }
                    var lured = fortDatas[i].LureInfo?.LureExpiresTimestampMs > DateTime.UtcNow.ToUnixTime();
                    var nMapObj = new NewMapObject("ps" + (lured ? "_lured" : ""), "PokeStop", fortDatas[i].Latitude,
                        fortDatas[i].Longitude, fortDatas[i].Id);
                    _openedSessions[session].MarkersQueue.Enqueue(nMapObj);
                }
                catch (Exception )//ex)
                {
                    i--;
                }
            }
        }
        #endregion

        #region Async Workers

        private async void MovePlayer()
        {
            const int delay = 25;
            while (!_windowClosing)
            {
                if (Bot != null && _playerMarker != null && Bot.Started)
                {
                    if (Bot.MoveRequired)
                    {
                        if (Bot.GotNewCoord)
                        {
                            // ReSharper disable once PossibleLossOfFraction
                            Bot.LatStep = (Bot.Lat - Bot._lat) / (2000 / delay);
                            // ReSharper disable once PossibleLossOfFraction
                            Bot.LngStep = (Bot.Lng - Bot._lng) / (2000 / delay);
                            Bot.GotNewCoord = false;
                            UpdateCoordBoxes();
                        }

                        Bot._lat += Bot.LatStep;
                        Bot._lng += Bot.LngStep;
                        _playerMarker.Position = new PointLatLng(Bot._lat, Bot._lng);
                        if (Math.Abs(Bot._lat - Bot.Lat) < 0.000000001 && Math.Abs(Bot._lng - Bot.Lng) < 0.000000001)
                            Bot.MoveRequired = false;
                    }
                }
                await Task.Delay(delay);
            }
        }


        private async void MarkersWorker()
        {
            while (!_windowClosing)
            {
                if (Bot?.MarkersQueue.Count > 0)
                {
                    try
                    {
                        var newMapObj = Bot.MarkersQueue.Dequeue();
                        switch (newMapObj.OType)
                        {
                            case "ps":
                                if (!Bot.MapMarkers.ContainsKey(newMapObj.Uid))
                                {
                                    var marker = new GMapMarker(new PointLatLng(newMapObj.Lat, newMapObj.Lng))
                                    {
                                        Shape = Properties.Resources.pstop.ToImage("PokeStop"),
                                        Offset = new Point(-16, -32),
                                        ZIndex = 5
                                    };
                                    pokeMap.Markers.Add(marker);
                                    Bot.MapMarkers.Add(newMapObj.Uid, marker);
                                }
                                break;
                            case "ps_lured":
                                if (!Bot.MapMarkers.ContainsKey(newMapObj.Uid))
                                {
                                    var marker = new GMapMarker(new PointLatLng(newMapObj.Lat, newMapObj.Lng))
                                    {
                                        Shape = Properties.Resources.pstop_lured.ToImage("Lured PokeStop"),
                                        Offset = new Point(-16, -32),
                                        ZIndex = 5
                                    };
                                    pokeMap.Markers.Add(marker);
                                    Bot.MapMarkers.Add(newMapObj.Uid, marker);
                                }
                                break;
                            case "pm_rm":
                                if (Bot.MapMarkers.ContainsKey(newMapObj.Uid))
                                {
                                    pokeMap.Markers.Remove(Bot.MapMarkers[newMapObj.Uid]);
                                    Bot.MapMarkers.Remove(newMapObj.Uid);
                                }
                                break;
                            case "forcemove_done":
                                if (Bot.ForceMoveMarker != null)
                                {
                                    pokeMap.Markers.Remove(Bot.ForceMoveMarker);
                                    Bot.ForceMoveMarker = null;
                                }
                                break;
                            case "pm":
                                if (!Bot.MapMarkers.ContainsKey(newMapObj.Uid))
                                {
                                    CreatePokemonMarker(newMapObj);
                                }
                                break;
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
                await Task.Delay(10);
            }
        }

        private void CreatePokemonMarker(NewMapObject newMapObj)
        {
            var pokemon = (PokemonId)Enum.Parse(typeof(PokemonId), newMapObj.OName);

            var marker = new GMapMarker(new PointLatLng(newMapObj.Lat, newMapObj.Lng))
            {
                Shape = pokemon.ToImage(),
                Offset = new Point(-15, -30),
                ZIndex = 10
            };
            pokeMap.Markers.Add(marker);
            Bot.MapMarkers.Add(newMapObj.Uid, marker);
        }

        private async void LogWorker()
        {
            while (!_windowClosing)
            {
                if (Bot?.LogQueue.Count > 0)
                {
                    var t = Bot.LogQueue.Dequeue();
                    Bot.Log.Add(t);
                    consoleBox.AppendParagraph(t.Item1, t.Item2);
                }
                await Task.Delay(10);
            }
        }

        #endregion

        #region Controll's events
        private void authBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            var comboBox = sender as ComboBox;
            if (comboBox != null)
                Bot.GlobalSettings.Auth.AuthType = (AuthType)comboBox.SelectedItem;
        }

        private void loginBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            var box = sender as TextBox;
            if (box == null) return;
            if (Bot.GlobalSettings.Auth.AuthType == AuthType.Google)
                Bot.GlobalSettings.Auth.GoogleUsername = box.Text;
            else
                Bot.GlobalSettings.Auth.PtcUsername = box.Text;
        }

        private void passwordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            var box = sender as PasswordBox;
            if (box == null) return;
            if (Bot.GlobalSettings.Auth.AuthType == AuthType.Google)
                Bot.GlobalSettings.Auth.GooglePassword = box.Password;
            else
                Bot.GlobalSettings.Auth.PtcPassword = box.Password;
        }

        private void button_Click(object sender, RoutedEventArgs e)
        {
            InputBox.Visibility = Visibility.Visible;
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            // YesButton Clicked! Let's hide our InputBox and handle the input text.
            InputBox.Visibility = Visibility.Collapsed;

            // Do something with the Input
            var input = InputTextBox.Text;

            var dir = Directory.CreateDirectory(subPath + "\\" + input);
            var settings = GlobalSettings.Load(dir.FullName) ?? GlobalSettings.Load(dir.FullName);
            InitBot(settings, input);
            // Clear InputBox.
            InputTextBox.Text = Empty;
        }

        private void InitBot(GlobalSettings settings, string profileName = "Unknown")
        {
            var newBot = CreateBowWindowData(settings, profileName);

            var session = new Session(newBot.Settings, newBot.Logic);
            session.Client.ApiFailure = new ApiFailureStrategy(session);

            session.EventDispatcher.EventReceived += evt => newBot.Listener.Listen(evt, session);
            session.EventDispatcher.EventReceived += evt => newBot.Aggregator.Listen(evt, session);
            session.Navigation.UpdatePositionEvent +=
                (lat, lng) => session.EventDispatcher.Send(new UpdatePositionEvent {Latitude = lat, Longitude = lng});

            newBot.Stats.DirtyEvent += () => { StatsOnDirtyEvent(newBot); };

            newBot._lat = settings.LocationSettings.DefaultLatitude;
            newBot._lng = settings.LocationSettings.DefaultLongitude;

            newBot.Machine.SetFailureState(new LoginState());

            _openedSessions.Add(session, newBot);

            #region bot panel UI
            Grid botGrid = new Grid()
            {
                Height = 120,
                Margin = new Thickness(0, 10, 0, 0)
            };
            Rectangle rec = new Rectangle()
            {
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Fill = new SolidColorBrush(Color.FromArgb(255, 97, 97, 97))
            };
            botGrid.Children.Add(rec);

            var r = FindResource("flatbutton") as Style;
            Button bStop = new Button()
            {
                Style = r,
                Content = "Stop",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 100,
                Height = 30,
                Margin = new Thickness(10, 80, 0, 0),
                Background = new LinearGradientBrush(Color.FromArgb(255, 238, 178, 156), Color.FromArgb(255, 192, 83, 83), new Point(1, 0.5), new Point(0, 0.05)) //(Color)ColorConverter.ConvertFromString("#FFEEB29C"), (Color)ColorConverter.ConvertFromString("#FFC05353")
            };
            Button bStart = new Button()
            {
                Style = r,
                Content = "Start",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Width = 100,
                Height = 30,
                Margin = new Thickness(136, 80, 0, 0),
                Background = new LinearGradientBrush(Color.FromArgb(255, 176, 238, 156), Color.FromArgb(255, 83, 192, 177), new Point(1, 0.5), new Point(0, 0.05))//(Color)ColorConverter.ConvertFromString("#FFB0EE9C"), (Color)ColorConverter.ConvertFromString("#FF53C0B1")
            };
            botGrid.Children.Add(bStop);
            botGrid.Children.Add(bStart);

            Label lbProfile = new Label()
            {
                Content = profileName,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 89, 195, 176)),//((Color)ColorConverter.ConvertFromString("#FF59C3B0")),
                FontSize = 18,
                Height = 38,
            };
            botGrid.Children.Add(lbProfile);

            Label lbLevel = new Label()
            {
                Content = "0",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 89, 195, 176)),//((Color)ColorConverter.ConvertFromString("#FF59C3B0")),
                FontSize = 14,
                Height = 38,
                Margin = new Thickness(0, 37, 0, 0)
            };
            botGrid.Children.Add(lbLevel);

            Label lvRuntime = new Label()
            {
                Content = "00:00",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = new SolidColorBrush(Color.FromArgb(255,89,195,176)),//Color)ColorConverter.ConvertFromString("#FF59C3B0")),
                FontSize = 14,
                Height = 38,
                Margin = new Thickness(152, 37, 0, 0)
            };
            botGrid.Children.Add(lvRuntime);

            newBot.Xpph = lbLevel;
            newBot.RunTime = lvRuntime;

            botPanel.Children.Add(botGrid);


            bStart.Click += delegate
            {
                if (!newBot.Started)
                {
                    session.Client.Player.SetCoordinates(newBot.GlobalSettings.LocationSettings.DefaultLatitude,
                        newBot.GlobalSettings.LocationSettings.DefaultLongitude,
                        newBot.GlobalSettings.LocationSettings.DefaultAltitude);
                    session.Client.Login = new PokemonGo.RocketAPI.Rpc.Login(session.Client);
                    newBot.Start();
                    newBot.Machine.AsyncStart(new VersionCheckState(), session, newBot.CancellationToken);
                    if (session.LogicSettings.UseSnipeLocationServer)
                        SnipePokemonTask.AsyncStart(session);                   
                }
            };

            bStop.Click += delegate
            {
                if (_curSession == session)
                {
                    ClearPokemonData();
                }
                newBot.Stop();
            };

            rec.MouseLeftButtonDown += delegate (object o, MouseButtonEventArgs args)
            {
                SelectBot(o, newBot, session);
            };

            if (_openedSessions.Count == 1)
            {
                SelectBot(rec, newBot, session);
            }
            #endregion
        }

        private void SelectBot(object o, BotWindowData newBot, Session session)
        {
            if (Bot != null)
            {
                Bot.GlobalSettings.StoreData(subPath + "\\" + Bot.ProfileName);
                Bot.EnqueData();
                ClearPokemonData();
            }
            foreach (var marker in newBot.MapMarkers.Values)
            {
                pokeMap.Markers.Add(marker);
            }
            _curSession = session;
            if (Bot != null)
            {
                pokeMap.Position = new PointLatLng(Bot._lat, Bot._lng);
                DrawPlayerMarker();
                StatsOnDirtyEvent(Bot);
            }
            foreach (var item in botPanel.GetLogicalChildCollection<Rectangle>())
            {
                item.Fill = !Equals(item, o) ? new SolidColorBrush(Color.FromArgb(255, 97, 97, 97)) : new SolidColorBrush(Color.FromArgb(255, 97, 97, 225));
            }
            RebuildUi();
        }

        private void UpdatePokemonCollection(ISession session)
        {
            if (Bot == null || session != _curSession) return;
            //PokeListBox.Items.Refresh();
            l_poke_inventory.Content = $"({Bot.PokemonList.Count}/{Bot.MaxPokemonStorageSize})";
        }

        // ReSharper disable once InconsistentNaming
        private void StatsOnDirtyEvent(BotWindowData _bot)
        {
            if (_bot == null) throw new ArgumentNullException(nameof(_bot));
            Dispatcher.BeginInvoke(new ThreadStart(_bot.UpdateXppH));
            if (Bot == _bot)
            {
                Dispatcher.BeginInvoke(new ThreadStart(delegate
                {
                    l_StarDust.Content = Bot.Stats?.TotalStardust;
                    l_Stardust_farmed.Content = Bot.Stats?.TotalStardust == 0 ? 0 : Bot.Stats?.TotalStardust - _curSession?.Profile?.PlayerData?.Currencies[1].Amount;
                    l_xp.Content = Bot.Stats?._exportStats?.CurrentXp;
                    l_xp_farmed.Content = Bot.Stats?.TotalExperience;
                    l_Pokemons_farmed.Content = Bot.Stats?.TotalPokemons;
                    l_Pokemons_transfered.Content = Bot.Stats?.TotalPokemonsTransfered;
                    l_Pokestops_farmed.Content = Bot.Stats?.TotalPokestops;
                    l_level.Content = Bot.Stats?._exportStats?.Level;
                    l_level_nextime.Content = $"{Bot.Stats?._exportStats?.HoursUntilLvl.ToString("00")}:{Bot.Stats?._exportStats?.MinutesUntilLevel.ToString("00")}";
                }));
            }
        }

        private void ClearPokemonData()
        {
            consoleBox.Document.Blocks.Clear();
            pokeMap.Markers.Clear();
            _playerMarker = null;
            Bot.LatStep = Bot.LngStep = 0;
        }

        private static BotWindowData CreateBowWindowData(GlobalSettings s, string name)
        {
            var stats = new Statistics();

            return new BotWindowData(name, s, new StateMachine(), stats, new StatisticsAggregator(stats),
                new WpfEventListener(), new ClientSettings(s), new LogicSettings(s));

        }

        private void RebuildUi()
        {
            if (Bot == null || _loadingUi) return;

            _loadingUi = true;
            settings_grid.IsEnabled = true;
            if (!tabControl.IsEnabled)
                tabControl.IsEnabled = true;

            authBox.SelectedItem = Bot.GlobalSettings.Auth.AuthType;
            if (Bot.GlobalSettings.Auth.AuthType == AuthType.Google)
            {
                loginBox.Text = Bot.GlobalSettings.Auth.GoogleUsername;
                passwordBox.Password = Bot.GlobalSettings.Auth.GooglePassword;
            }
            else
            {
                loginBox.Text = Bot.GlobalSettings.Auth.PtcUsername;
                passwordBox.Password = Bot.GlobalSettings.Auth.PtcPassword;
            }

            #region Mapping settings to UIElements
            foreach (var uiElem in settings_grid.GetLogicalChildCollection<TextBox>())
            {
                string val;
                if (Extensions.GetValueByName(uiElem.Name.Substring(2), Bot.GlobalSettings, out val))
                    uiElem.Text = val;
            }

            foreach (var uiElem in settings_grid.GetLogicalChildCollection<PasswordBox>())
            {
                string val;
                if (Extensions.GetValueByName(uiElem.Name.Substring(2), Bot.GlobalSettings, out val))
                    uiElem.Password = val;
            }

            foreach (var uiElem in settings_grid.GetLogicalChildCollection<CheckBox>())
            {
                bool val;
                if (Extensions.GetValueByName(uiElem.Name.Substring(2), Bot.GlobalSettings, out val))
                    uiElem.IsChecked = val;
            }
            #endregion

            PokeListBox.ItemsSource = Bot.PokemonList;

            _loadingUi = false;
        }


        #region Windows UI Methods
        private void UpdateCoordBoxes()
        {
            c_DefaultLatitude.Text = Bot.GlobalSettings.LocationSettings.DefaultLatitude.ToString(CultureInfo.InvariantCulture);
            c_DefaultLongitude.Text = Bot.GlobalSettings.LocationSettings.DefaultLongitude.ToString(CultureInfo.InvariantCulture);
        }


        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            // NoButton Clicked! Let's hide our InputBox.
            InputBox.Visibility = Visibility.Collapsed;

            // Clear InputBox.
            InputTextBox.Text = Empty;
        }

        private void pokeMap_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var mousePos = e.GetPosition(pokeMap);
            //Getting real coordinates from mouse click
            var mapPos = pokeMap.FromLocalToLatLng((int)mousePos.X, (int)mousePos.Y);
            var lat = mapPos.Lat;
            var lng = mapPos.Lng;

            if (Bot != null)
            {
                if (Bot.Started)
                {
                    if (Bot.ForceMoveMarker == null)
                    {
                        Bot.ForceMoveMarker = new GMapMarker(mapPos)
                        {
                            Shape = Properties.Resources.force_move.ToImage(),
                            Offset = new Point(-24, -48),
                            ZIndex = int.MaxValue
                        };
                        pokeMap.Markers.Add(Bot.ForceMoveMarker);
                    }
                    else
                    {
                        Bot.ForceMoveMarker.Position = mapPos;
                    }
                    _curSession.StartForceMove(lat, lng);
                }
                else
                {
                    Bot.Lat = Bot._lat = lat;
                    Bot.Lng = Bot._lng = lng;
                    Bot.GlobalSettings.LocationSettings.DefaultLatitude = lat;
                    Bot.GlobalSettings.LocationSettings.DefaultLongitude = lng;
                    DrawPlayerMarker();
                    UpdateCoordBoxes();
                }
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _windowClosing = true;
            if (Bot == null || _loadingUi) return;
            Bot.GlobalSettings.StoreData(subPath + "\\" + Bot.ProfileName);
            foreach (var b in _openedSessions.Values)
            {
                b.Stop();
            }
        }

        private void SortByCpClick(object sender, RoutedEventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            PokeListBox.Items.SortDescriptions.Clear();
            PokeListBox.Items.SortDescriptions.Add(new SortDescription("Cp", ListSortDirection.Descending));
        }
        private void SortByIvClick(object sender, RoutedEventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            PokeListBox.Items.SortDescriptions.Clear();
            PokeListBox.Items.SortDescriptions.Add(new SortDescription("Iv", ListSortDirection.Descending));
        }
        private void sortByAz_Click(object sender, RoutedEventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            PokeListBox.Items.SortDescriptions.Clear();
            PokeListBox.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
        }
        #endregion

        #region Property <-> Settings

        private void HandleUiElementChangedEvent(object uiElement)
        {
            var box = uiElement as TextBox;
            if (box != null)
            {
                var propName = box.Name.Replace("c_", "");
                Extensions.SetValueByName(propName, box.Text, Bot.GlobalSettings);
                return;
            }
            var checkBox = uiElement as CheckBox;
            if (checkBox != null)
            {
                var propName = checkBox.Name.Replace("c_", "");
                Extensions.SetValueByName(propName, checkBox.IsChecked, Bot.GlobalSettings);
            }
            var passBox = uiElement as PasswordBox;
            if (passBox != null)
            {
                var propName = passBox.Name.Replace("c_", "");
                Extensions.SetValueByName(propName, passBox.Password, Bot.GlobalSettings);
            }
        }

        private void BotPropertyChanged(object sender, EventArgs e)
        {
            if (Bot == null || _loadingUi) return;
            HandleUiElementChangedEvent(sender);
        }
        #endregion

        #endregion


        #region Android Device Tests

        private void b_getDataFromRealPhone_Click(object sender, RoutedEventArgs e)
        {
            StartFillFromRealDevice();
        }

        private async void StartFillFromRealDevice()
        {
            var dd = await Adb.GetDeviceData();
            c_DeviceId.Text = Bot.GlobalSettings.Device.DeviceId = dd.DeviceId;
            c_AndroidBoardName.Text = Bot.GlobalSettings.Device.AndroidBoardName = dd.AndroidBoardName;
            c_AndroidBootloader.Text = Bot.GlobalSettings.Device.AndroidBootLoader = dd.AndroidBootloader;
            c_DeviceBrand.Text = Bot.GlobalSettings.Device.DeviceBrand = dd.DeviceBrand;
            c_DeviceModel.Text = Bot.GlobalSettings.Device.DeviceModel = dd.DeviceModel;
            c_DeviceModelIdentifier.Text = Bot.GlobalSettings.Device.DeviceModelIdentifier = dd.DeviceModelIdentifier;
            c_HardwareManufacturer.Text = Bot.GlobalSettings.Device.HardwareManufacturer = dd.HardwareManufacturer;
            c_HardwareModel.Text = Bot.GlobalSettings.Device.HardWareModel = dd.HardwareModel;
            c_FirmwareBrand.Text = Bot.GlobalSettings.Device.FirmwareBrand = dd.FirmwareBrand;
            c_FirmwareTags.Text = Bot.GlobalSettings.Device.FirmwareTags = dd.FirmwareTags;
            c_FirmwareType.Text = Bot.GlobalSettings.Device.FirmwareType = dd.FirmwareType;
            c_FirmwareFingerprint.Text = Bot.GlobalSettings.Device.FirmwareFingerprint = dd.FirmwareFingerprint;
        }

        private void b_generateRandomDeviceId_Click(object sender, RoutedEventArgs e)
        {
            c_DeviceId.Text = DeviceSettings.RandomString(16, "0123456789abcdef");
        }
        #endregion


        internal class NewMapObject
        {
            public string OType;
            public string OName;
            public double Lat;
            public double Lng;
            internal string Uid;
            public NewMapObject(string oType, string oName, double lat, double lng, string uid)
            {
                OType = oType;
                OName = oName;
                Lat = lat;
                Lng = lng;
                Uid = uid;
            }
        }

        private class BotWindowData
        {
            public readonly string ProfileName;
            private CancellationTokenSource _cts;
            public CancellationToken CancellationToken => _cts.Token;
            internal GMapMarker ForceMoveMarker;
            public List<Tuple<string, Color>> Log = new List<Tuple<string, Color>>();
            public Queue<Tuple<string, Color>> LogQueue = new Queue<Tuple<string, Color>>();
            public Dictionary<string, GMapMarker> MapMarkers = new Dictionary<string, GMapMarker>();
            public Queue<NewMapObject> MarkersQueue = new Queue<NewMapObject>();
            public readonly StateMachine Machine;
            public readonly Statistics Stats;
            public readonly StatisticsAggregator Aggregator;
            public readonly WpfEventListener Listener;
            public readonly ClientSettings Settings;
            public readonly LogicSettings Logic;
            public readonly GlobalSettings GlobalSettings;
            public int MaxItemStorageSize;
            public int MaxPokemonStorageSize;
            public ObservableCollection<PokemonUiData> PokemonList = new ObservableCollection<PokemonUiData>();
            public ObservableCollection<ItemUiData> ItemList = new ObservableCollection<ItemUiData>();

            public Label RunTime;
            public Label Xpph;
            public bool Started;

            private readonly DispatcherTimer _timer;
            private TimeSpan _ts;

            public double Lat;
            public double Lng;
            public bool GotNewCoord;
            public bool MoveRequired;
            private double _la, _ln;

            // ReSharper disable once InconsistentNaming
            internal double _lat
            {
                get { return _la; }
                set
                {
                    GlobalSettings.LocationSettings.DefaultLatitude = value;
                    _la = value;
                }
            }

            // ReSharper disable once InconsistentNaming
            internal double _lng
            {
                get { return _ln; }
                set
                {
                    GlobalSettings.LocationSettings.DefaultLongitude = value;
                    _ln = value;
                }
            }
            public double LatStep, LngStep;

            public BotWindowData(string name, GlobalSettings gs, StateMachine sm, Statistics st, StatisticsAggregator sa, WpfEventListener wel, ClientSettings cs, LogicSettings l)
            {
                ProfileName = name;
                Settings = new ClientSettings(gs);
                Logic = new LogicSettings(gs);
                GlobalSettings = gs;
                Machine = sm;
                Stats = st;
                Aggregator = sa;
                Listener = wel;
                Settings = cs;
                Logic = l;

                _ts = new TimeSpan();
                _timer = new DispatcherTimer { Interval = new TimeSpan(0, 0, 1) };
                _timer.Tick += delegate
                {
                    _ts += new TimeSpan(0, 0, 1);
                    RunTime.Content = _ts.ToString();
                };
                _cts = new CancellationTokenSource();
            }

            public void UpdateXppH()
            {
                if (Stats == null || Math.Abs(_ts.TotalHours) < 0.0000001)
                    Xpph.Content = 0;
                else
                    Xpph.Content = "Xp/h: " + (Stats.TotalExperience / _ts.TotalHours).ToString("0.0");
            }

            private void WipeData()
            {
                Log = new List<Tuple<string, Color>>();
                MapMarkers = new Dictionary<string, GMapMarker>();
                MarkersQueue = new Queue<NewMapObject>();
                LogQueue = new Queue<Tuple<string, Color>>();
            }

            public void Stop()
            {
                TimerStop();
                _cts.Cancel();
                WipeData();
                _ts = new TimeSpan();
                Started = false;
            }

            public void Start()
            {
                TimerStart();
                _cts.Dispose();
                _cts = new CancellationTokenSource();
                Started = true;
            }

            private void TimerStart() => _timer?.Start();

            private void TimerStop() => _timer?.Stop();

            internal void EnqueData()
            {
                while (LogQueue.Count > 0)
                    Log.Add(LogQueue.Dequeue());
                foreach (var item in Log)
                    LogQueue.Enqueue(item);
                Log = new List<Tuple<string, Color>>();
            }
        }

        public class PokemonUiData
        {
            public ulong Id { get; set; }
            public BitmapSource Image { get; set; }
            public string Name { get; set; }
            public int Cp { get; set; }
            public double Iv { get; set; }

            public PokemonUiData(ulong id, BitmapSource img, string name, int cp, double iv)
            {
                Id = id;
                Image = img;
                Name = name;
                Cp = cp;
                Iv = iv;
            }
        }
        public class ItemUiData : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
            public ItemId Id { get; set; }
            public BitmapSource Image { get; set; }
            public string Name { get; set; }
            private int _amount;
            public int Amount
            {
                get { return _amount; }
                set
                {
                    _amount = value;
                    OnPropertyChanged();
                }
            }

            public ItemUiData(ItemId id, BitmapSource img, string name, int amount)
            {
                Id = id;
                Image = img;
                Name = name;
                Amount = amount;
            }
        }


    }
}
