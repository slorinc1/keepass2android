/*
This file is part of Keepass2Android, Copyright 2013 Philipp Crocoll. 

  Keepass2Android is free software: you can redistribute it and/or modify
  it under the terms of the GNU General Public License as published by
  the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  Keepass2Android is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  GNU General Public License for more details.

  You should have received a copy of the GNU General Public License
  along with Keepass2Android.  If not, see <http://www.gnu.org/licenses/>.
  */

using System;
using System.Collections.Generic;
using System.Linq;
using Java.Util;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Widget;
using Android.Preferences;
using KeePassLib;
using KeePassLib.Utility;
using Android.Views.InputMethods;
using KeePass.Util.Spr;

namespace keepass2android
{
	/// <summary>
	/// Service to show the notifications to make the current entry accessible through clipboard or the KP2A keyboard.
	/// </summary>
	/// The name reflects only the possibility through clipboard because keyboard was introduced later.
	/// The notifications require to be displayed by a service in order to be kept when the activity is closed
	/// after searching for a URL.
	[Service]
	public class CopyToClipboardService : Service
	{


		public const int NotifyUsername = 1;
		public const int NotifyPassword = 2;
		public const int NotifyKeyboard = 3;
		public const int ClearClipboard = 4;

		static public void CopyValueToClipboardWithTimeout(Context ctx, string text)
		{
			Intent i = new Intent(ctx, typeof(CopyToClipboardService));
			i.SetAction(Intents.CopyStringToClipboard);
			i.PutExtra(_stringtocopy, text);
			ctx.StartService(i);
		}

		static public void ActivateKeyboard(Context ctx)
		{
			Intent i = new Intent(ctx, typeof(CopyToClipboardService));
			i.SetAction(Intents.ActivateKeyboard);
			ctx.StartService(i);
		}

		public static void CancelNotifications(Context ctx)
		{

			Intent i = new Intent(ctx, typeof(CopyToClipboardService));
			i.SetAction(Intents.ClearNotificationsAndData);
			ctx.StartService(i);
		}

		public CopyToClipboardService(IntPtr javaReference, JniHandleOwnership transfer)
			: base(javaReference, transfer)
		{
		}

		NotificationDeletedBroadcastReceiver _notificationDeletedBroadcastReceiver;
		StopOnLockBroadcastReceiver _stopOnLockBroadcastReceiver;

		public CopyToClipboardService()
		{


		}

		public override IBinder OnBind(Intent intent)
		{
			return null;
		}

		public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
		{
			Kp2aLog.Log("Received intent to provide access to entry");

			_stopOnLockBroadcastReceiver = new StopOnLockBroadcastReceiver(this);
			IntentFilter filter = new IntentFilter();
			filter.AddAction(Intents.DatabaseLocked);
			RegisterReceiver(_stopOnLockBroadcastReceiver, filter);

			if ((intent.Action == Intents.ShowNotification) || (intent.Action == Intents.UpdateKeyboard))
			{
				String uuidBytes = intent.GetStringExtra(EntryActivity.KeyEntry);

				PwUuid entryId = PwUuid.Zero;
				if (uuidBytes != null)
					entryId = new PwUuid(MemUtil.HexStringToByteArray(uuidBytes));

				PwEntryOutput entry;
				try
				{
					if ((App.Kp2a.GetDb().LastOpenedEntry != null)
						&& (entryId.Equals(App.Kp2a.GetDb().LastOpenedEntry.Uuid)))
					{
						entry = App.Kp2a.GetDb().LastOpenedEntry;
					}
					else
					{
						entry = new PwEntryOutput(App.Kp2a.GetDb().Entries[entryId], App.Kp2a.GetDb().KpDatabase);
					}

				}
				catch (Exception)
				{
					//seems like restarting the service happened after closing the DB
					StopSelf();
					return StartCommandResult.NotSticky;
				}

				if (intent.Action == Intents.ShowNotification)
				{
					//first time opening the entry -> bring up the notifications
					bool closeAfterCreate = intent.GetBooleanExtra(EntryActivity.KeyCloseAfterCreate, false);
					DisplayAccessNotifications(entry, closeAfterCreate);
				}
				else //UpdateKeyboard
				{
#if !EXCLUDE_KEYBOARD
					//this action is received when the data in the entry has changed (e.g. by plugins)
					//update the keyboard data.
					//Check if keyboard is (still) available
					if (Keepass2android.Kbbridge.KeyboardData.EntryId == entry.Uuid.ToHexString())
						MakeAccessibleForKeyboard(entry);
#endif
				}
			}
			if (intent.Action == Intents.CopyStringToClipboard)
			{

				TimeoutCopyToClipboard(intent.GetStringExtra(_stringtocopy));
			}
			if (intent.Action == Intents.ActivateKeyboard)
			{
				ActivateKp2aKeyboard();
			}
			if (intent.Action == Intents.ClearNotificationsAndData)
			{
				ClearNotifications();
			}


			return StartCommandResult.RedeliverIntent;
		}

		private void OnLockDatabase()
		{
			Kp2aLog.Log("Stopping clipboard service due to database lock");

			StopSelf();
		}

		private NotificationManager _notificationManager;
		private int _numElementsToWaitFor;

		public override void OnDestroy()
		{
			Kp2aLog.Log("CopyToClipboardService.OnDestroy");

			// These members might never get initialized if the app timed out
			if (_stopOnLockBroadcastReceiver != null)
			{
				UnregisterReceiver(_stopOnLockBroadcastReceiver);
			}
			if (_notificationDeletedBroadcastReceiver != null)
			{
				UnregisterReceiver(_notificationDeletedBroadcastReceiver);
			}
			if (_notificationManager != null)
			{
				_notificationManager.Cancel(NotifyPassword);
				_notificationManager.Cancel(NotifyUsername);
				_notificationManager.Cancel(NotifyKeyboard);

				_numElementsToWaitFor = 0;
				ClearKeyboard(true);
			}
			if (_clearClipboardTask != null)
			{
				Kp2aLog.Log("Clearing clipboard due to stop CopyToClipboardService");
				_clearClipboardTask.Run();
			}

			Kp2aLog.Log("Destroyed Show-Notification-Receiver.");

			base.OnDestroy();
		}

		private const string ActionNotificationCancelled = "notification_cancelled";

		//creates a delete intent (started when notification is cancelled by user or something else)
		//requires different request codes for every item (otherwise the intents are identical)
		PendingIntent CreateDeleteIntent(int requestCode)
		{
			Intent intent = new Intent(ActionNotificationCancelled);
			Bundle extra = new Bundle();
			extra.PutInt("requestCode", requestCode);
			intent.PutExtras(extra);

			return PendingIntent.GetBroadcast(this, requestCode, intent, PendingIntentFlags.CancelCurrent);
		}

		public void DisplayAccessNotifications(PwEntryOutput entry, bool closeAfterCreate)
		{
			var hadKeyboardData = ClearNotifications();

			String entryName = entry.OutputStrings.ReadSafe(PwDefs.TitleField);

			ISharedPreferences prefs = PreferenceManager.GetDefaultSharedPreferences(this);
			if (prefs.GetBoolean(GetString(Resource.String.CopyToClipboardNotification_key), Resources.GetBoolean(Resource.Boolean.CopyToClipboardNotification_default)))
			{

				if (entry.OutputStrings.ReadSafe(PwDefs.PasswordField).Length > 0)
				{
					// only show notification if password is available
					Notification password = GetNotification(Intents.CopyPassword, Resource.String.copy_password, Resource.Drawable.notify, entryName);

					password.DeleteIntent = CreateDeleteIntent(NotifyPassword);
					_notificationManager.Notify(NotifyPassword, password);
					_numElementsToWaitFor++;

				}

				if (entry.OutputStrings.ReadSafe(PwDefs.UserNameField).Length > 0)
				{
					// only show notification if username is available
					Notification username = GetNotification(Intents.CopyUsername, Resource.String.copy_username, Resource.Drawable.notify, entryName);
					username.DeleteIntent = CreateDeleteIntent(NotifyUsername);
					_numElementsToWaitFor++;
					_notificationManager.Notify(NotifyUsername, username);
				}
			}

			bool hasKeyboardDataNow = false;
			if (prefs.GetBoolean(GetString(Resource.String.UseKp2aKeyboard_key), Resources.GetBoolean(Resource.Boolean.UseKp2aKeyboard_default)))
			{

				//keyboard
				hasKeyboardDataNow = MakeAccessibleForKeyboard(entry);
				if (hasKeyboardDataNow)
				{
					// only show notification if username is available
					Notification keyboard = GetNotification(Intents.CheckKeyboard, Resource.String.available_through_keyboard, Resource.Drawable.notify_keyboard, entryName);
					keyboard.DeleteIntent = CreateDeleteIntent(NotifyKeyboard);
					_numElementsToWaitFor++;
					_notificationManager.Notify(NotifyKeyboard, keyboard);

					//if the app is about to be closed again (e.g. after searching for a URL and returning to the browser:
					// automatically bring up the Keyboard selection dialog
					if ((closeAfterCreate) && prefs.GetBoolean(GetString(Resource.String.OpenKp2aKeyboardAutomatically_key), Resources.GetBoolean(Resource.Boolean.OpenKp2aKeyboardAutomatically_default)))
					{
						ActivateKp2aKeyboard();
					}
				}

			}

			if ((!hasKeyboardDataNow) && (hadKeyboardData))
			{
				ClearKeyboard(true); //this clears again and then (this is the point) broadcasts that we no longer have keyboard data
			}

			if (_numElementsToWaitFor == 0)
			{
				StopSelf();
				return;
			}

			IntentFilter filter = new IntentFilter();
			filter.AddAction(Intents.CopyUsername);
			filter.AddAction(Intents.CopyPassword);
			filter.AddAction(Intents.CheckKeyboard);

			//register receiver to get notified when notifications are discarded in which case we can shutdown the service
			_notificationDeletedBroadcastReceiver = new NotificationDeletedBroadcastReceiver(this);
			IntentFilter deletefilter = new IntentFilter();
			deletefilter.AddAction(ActionNotificationCancelled);
			RegisterReceiver(_notificationDeletedBroadcastReceiver, deletefilter);
		}

		private bool ClearNotifications()
		{
			// Notification Manager
			_notificationManager = (NotificationManager)GetSystemService(NotificationService);

			_notificationManager.Cancel(NotifyPassword);
			_notificationManager.Cancel(NotifyUsername);
			_notificationManager.Cancel(NotifyKeyboard);
			_numElementsToWaitFor = 0;
			bool hadKeyboardData = ClearKeyboard(false); //do not broadcast if the keyboard was changed
			return hadKeyboardData;
		}

		bool MakeAccessibleForKeyboard(PwEntryOutput entry)
		{
#if EXCLUDE_KEYBOARD
			return false;
#else
			bool hasData = false;
			Keepass2android.Kbbridge.KeyboardDataBuilder kbdataBuilder = new Keepass2android.Kbbridge.KeyboardDataBuilder();

			String[] keys = {PwDefs.UserNameField, 
				PwDefs.PasswordField, 
				PwDefs.UrlField,
				PwDefs.NotesField,
				PwDefs.TitleField
			};
			int[] resIds = {Resource.String.entry_user_name,
				Resource.String.entry_password,
				Resource.String.entry_url,
				Resource.String.entry_comment,
				Resource.String.entry_title };

			//add standard fields:
			int i=0;
			foreach (string key in keys)
			{
				String value = entry.OutputStrings.ReadSafe(key);

				if (value.Length > 0)
				{
					kbdataBuilder.AddString(key, GetString(resIds[i]), value);
					hasData = true;
				}
				i++;
			}
			//add additional fields:
			foreach (var pair in entry.OutputStrings)
			{
				var key = pair.Key;
				var value = pair.Value.ReadString();

				if (!PwDefs.IsStandardField(key)) {
					kbdataBuilder.AddString(pair.Key, pair.Key, value);
					hasData = true;
				}
			}


			kbdataBuilder.Commit();
			Keepass2android.Kbbridge.KeyboardData.EntryName = entry.OutputStrings.ReadSafe(PwDefs.TitleField);
			Keepass2android.Kbbridge.KeyboardData.EntryId = entry.Uuid.ToHexString();

			return hasData;
#endif
		}


		public void OnWaitElementDeleted(int itemId)
		{
			_numElementsToWaitFor--;
			if (_numElementsToWaitFor <= 0)
			{
				StopSelf();
			}
			if (itemId == NotifyKeyboard)
			{
				//keyboard notification was deleted -> clear entries in keyboard
				ClearKeyboard(true);
			}
		}

		bool ClearKeyboard(bool broadcastClear)
		{
#if !EXCLUDE_KEYBOARD
			Keepass2android.Kbbridge.KeyboardData.AvailableFields.Clear();
			Keepass2android.Kbbridge.KeyboardData.EntryName = null;
			bool hadData = Keepass2android.Kbbridge.KeyboardData.EntryId != null;
			Keepass2android.Kbbridge.KeyboardData.EntryId = null;

			if ((hadData) && broadcastClear)
				SendBroadcast(new Intent(Intents.KeyboardCleared));

			return hadData;
#else
			return false;
#endif
		}

		private readonly Timer _timer = new Timer();

		internal void TimeoutCopyToClipboard(String text)
		{
			Util.CopyToClipboard(this, text);

			ISharedPreferences prefs = PreferenceManager.GetDefaultSharedPreferences(this);
			String sClipClear = prefs.GetString(GetString(Resource.String.clipboard_timeout_key), GetString(Resource.String.clipboard_timeout_default));

			long clipClearTime = long.Parse(sClipClear);

			_clearClipboardTask = new ClearClipboardTask(this, text, _uiThreadCallback);
			if (clipClearTime > 0)
			{
				_numElementsToWaitFor++;
				_timer.Schedule(_clearClipboardTask, clipClearTime);
			}
		}

		// Task which clears the clipboard, and sends a toast to the foreground.
		private class ClearClipboardTask : TimerTask
		{

			private readonly String _clearText;
			private readonly CopyToClipboardService _service;
			private readonly Handler _handler;

			public ClearClipboardTask(CopyToClipboardService service, String clearText, Handler handler)
			{
				_clearText = clearText;
				_service = service;
				_handler = handler;
			}

			public override void Run()
			{
				String currentClip = Util.GetClipboard(_service);
				_handler.Post(() => _service.OnWaitElementDeleted(ClearClipboard));
				if (currentClip.Equals(_clearText))
				{
					Util.CopyToClipboard(_service, "");
					_handler.Post(() =>
					{
						Toast.MakeText(_service, Resource.String.ClearClipboard, ToastLength.Long).Show();
					});
				}
			}
		}


		// Setup to allow the toast to happen in the foreground
		readonly Handler _uiThreadCallback = new Handler();
		private ClearClipboardTask _clearClipboardTask;
		private const string _stringtocopy = "StringToCopy";



		private Notification GetNotification(String intentText, int descResId, int drawableResId, String entryName)
		{
			String desc = GetString(descResId);

			String title = GetString(Resource.String.app_name);
			if (!String.IsNullOrEmpty(entryName))
				title += " (" + entryName + ")";


			Notification notify = new Notification(drawableResId, desc, Java.Lang.JavaSystem.CurrentTimeMillis());

			Intent intent = new Intent(intentText);
			intent.SetPackage(PackageName);
			PendingIntent pending = PendingIntent.GetBroadcast(this, descResId, intent, PendingIntentFlags.CancelCurrent);

			notify.SetLatestEventInfo(this, title, desc, pending);

			return notify;
		}

		private class StopOnLockBroadcastReceiver : BroadcastReceiver
		{
			readonly CopyToClipboardService _service;
			public StopOnLockBroadcastReceiver(CopyToClipboardService service)
			{
				_service = service;
			}

			public override void OnReceive(Context context, Intent intent)
			{
				switch (intent.Action)
				{
					case Intents.DatabaseLocked:
						_service.OnLockDatabase();
						break;
				}
			}
		}



		class NotificationDeletedBroadcastReceiver : BroadcastReceiver
		{
			readonly CopyToClipboardService _service;
			public NotificationDeletedBroadcastReceiver(CopyToClipboardService service)
			{
				_service = service;
			}

			#region implemented abstract members of BroadcastReceiver
			public override void OnReceive(Context context, Intent intent)
			{
				if (intent.Action == ActionNotificationCancelled)
				{
					_service.OnWaitElementDeleted(intent.Extras.GetInt("requestCode"));
				}
			}
			#endregion
		}

		internal void ActivateKp2aKeyboard()
		{
			string currentIme = Android.Provider.Settings.Secure.GetString(
								ContentResolver,
								Android.Provider.Settings.Secure.DefaultInputMethod);

			string kp2aIme = PackageName + "/keepass2android.softkeyboard.KP2AKeyboard";

			InputMethodManager imeManager = (InputMethodManager)ApplicationContext.GetSystemService(InputMethodService);
			if (imeManager == null)
			{
				Toast.MakeText(this, Resource.String.not_possible_im_picker, ToastLength.Long).Show();
				return;
			}

			if (currentIme == kp2aIme)
			{
				imeManager.ToggleSoftInput(ShowFlags.Forced, HideSoftInputFlags.None);
			}
			else
			{

				IList<InputMethodInfo> inputMethodProperties = imeManager.EnabledInputMethodList;

				if (!inputMethodProperties.Any(imi => imi.Id.Equals(kp2aIme)))
				{
					Toast.MakeText(this, Resource.String.please_activate_keyboard, ToastLength.Long).Show();
					Intent settingsIntent = new Intent(Android.Provider.Settings.ActionInputMethodSettings);
					settingsIntent.SetFlags(ActivityFlags.NewTask);
					StartActivity(settingsIntent);
				}
				else
				{
#if !EXCLUDE_KEYBOARD
	                Keepass2android.Kbbridge.ImeSwitcher.SwitchToKeyboard(this, kp2aIme, false);
#endif
				}
			}
		}


	}

	[BroadcastReceiver(Permission = "keepass2android." + AppNames.PackagePart + ".permission.CopyToClipboard")]
	[IntentFilter(new[] { Intents.CopyUsername, Intents.CopyPassword, Intents.CheckKeyboard })]
	class CopyToClipboardBroadcastReceiver : BroadcastReceiver
	{
		public CopyToClipboardBroadcastReceiver(IntPtr javaReference, JniHandleOwnership transfer)
			: base(javaReference, transfer)
		{
		}


		public CopyToClipboardBroadcastReceiver()
		{
		}

		public override void OnReceive(Context context, Intent intent)
		{
			String action = intent.Action;

			//check if we have a last opened entry
			//this should always be non-null, but if the OS has killed the app, it might occur.
			if (App.Kp2a.GetDb().LastOpenedEntry == null)
			{
				Intent i = new Intent(context, typeof(AppKilledInfo));
				i.SetFlags(ActivityFlags.ClearTask | ActivityFlags.NewTask);
				context.StartActivity(i);
				return;
			}

			if (action.Equals(Intents.CopyUsername))
			{
				String username = App.Kp2a.GetDb().LastOpenedEntry.OutputStrings.ReadSafe(PwDefs.UserNameField);
				if (username.Length > 0)
				{
					CopyToClipboardService.CopyValueToClipboardWithTimeout(context, username);
				}
			}
			else if (action.Equals(Intents.CopyPassword))
			{
				String password = App.Kp2a.GetDb().LastOpenedEntry.OutputStrings.ReadSafe(PwDefs.PasswordField);
				if (password.Length > 0)
				{
					CopyToClipboardService.CopyValueToClipboardWithTimeout(context, password);
				}
			}
			else if (action.Equals(Intents.CheckKeyboard))
			{
				CopyToClipboardService.ActivateKeyboard(context);
			}
		}

	};
}

