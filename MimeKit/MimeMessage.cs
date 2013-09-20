//
// MimeMessage.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2013 Jeffrey Stedfast
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;

namespace MimeKit {
	public sealed class MimeMessage
	{
		static readonly StringComparer icase = StringComparer.OrdinalIgnoreCase;
		static readonly string[] StandardAddressHeaders = new string[] {
			"Sender", "From", "Reply-To", "To", "Cc", "Bcc"
		};

		Dictionary<string, InternetAddressList> addresses;
		IList<string> inreplyto, references;
		DateTimeOffset date;
		string messageId;
		Version version;

		internal MimeMessage (ParserOptions options, IEnumerable<Header> headers)
		{
			addresses = new Dictionary<string, InternetAddressList> (icase);
			Headers = new HeaderList (options);

			// initialize our address lists
			foreach (var name in StandardAddressHeaders) {
				var list = new InternetAddressList ();
				list.Changed += InternetAddressListChanged;
				addresses.Add (name, list);
			}

			references = new List<string> ();
			inreplyto = new List<string> ();

			Headers.Changed += HeadersChanged;

			// add all of our message headers...
			foreach (var header in headers) {
				if (header.Field.StartsWith ("Content-", StringComparison.OrdinalIgnoreCase))
					continue;

				Headers.Add (header);
			}
		}

		internal MimeMessage (ParserOptions options)
		{
			addresses = new Dictionary<string, InternetAddressList> (icase);
			Headers = new HeaderList (options);

			// initialize our address lists
			foreach (var name in StandardAddressHeaders) {
				var list = new InternetAddressList ();
				list.Changed += InternetAddressListChanged;
				addresses.Add (name, list);
			}

			references = new List<string> ();
			inreplyto = new List<string> ();

			Headers.Changed += HeadersChanged;
		}

		public MimeMessage () : this (ParserOptions.Default.Clone ())
		{
			Headers["From"] = string.Empty;
			Headers["To"] = string.Empty;
			Date = DateTimeOffset.Now;
			Subject = string.Empty;
		}

		public HeaderList Headers {
			get; private set;
		}

		public InternetAddressList Sender {
			get { return addresses["Sender"]; }
		}

		public InternetAddressList From {
			get { return addresses["From"]; }
		}

		public InternetAddressList ReplyTo {
			get { return addresses["Reply-To"]; }
		}

		public InternetAddressList To {
			get { return addresses["To"]; }
		}

		public InternetAddressList Cc {
			get { return addresses["Cc"]; }
		}

		public InternetAddressList Bcc {
			get { return addresses["Bcc"]; }
		}

		public string Subject {
			get { return Headers["Subject"]; }
			set {
				if (value == null)
					throw new ArgumentNullException ("value");

				Headers.Changed -= HeadersChanged;
				Headers["Subject"] = value;
				Headers.Changed += HeadersChanged;
			}
		}

		public DateTimeOffset Date {
			get { return date; }
			set {
				if (date == value)
					return;

				date = value;

				Headers.Changed -= HeadersChanged;
				Headers["Date"] = DateUtils.ToString (date);
				Headers.Changed += HeadersChanged;
			}
		}

		public string[] References {
			get { return references.ToArray (); }
			// FIXME: implement a setter
		}

		public string[] InReplyTo {
			get { return inreplyto.ToArray (); }
			// FIXME: implement a setter
		}

		public string MessageId {
			get { return messageId; }
			set {
				if (value == null)
					throw new ArgumentNullException ("value");

				if (messageId == value)
					return;

				var buffer = Encoding.ASCII.GetBytes (value);
				InternetAddress addr;
				int index = 0;

				if (!InternetAddress.TryParse (Headers.Options, buffer, ref index, buffer.Length, false, out addr) || !(addr is MailboxAddress))
					throw new ArgumentException ("Invalid Message-Id format.");

				messageId = "<" + ((MailboxAddress) addr).Address + ">";

				Headers.Changed -= HeadersChanged;
				Headers["Message-Id"] = messageId;
				Headers.Changed += HeadersChanged;
			}
		}

		public Version MimeVersion {
			get { return version; }
			set {
				if (value == null)
					throw new ArgumentNullException ("value");

				if (version != null && version.CompareTo (value) == 0)
					return;

				version = value;

				Headers.Changed -= HeadersChanged;
				Headers["MIME-Version"] = version.ToString ();
				Headers.Changed += HeadersChanged;
			}
		}

		public MimeEntity Body {
			get; set;
		}

		public void WriteTo (Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException ("stream");

			if (!Headers.Contains ("Date"))
				Date = DateTimeOffset.Now;

			if (messageId == null)
				MessageId = MimeUtils.GenerateMessageId ();

			if (version == null && Body != null && Body.Headers.Count > 0)
				MimeVersion = new Version (1, 0);

			Headers.WriteTo (stream);

			if (Body == null) {
				stream.WriteByte ((byte) '\n');
			} else {
				Body.WriteTo (stream);
			}
		}

		void SerializeAddressList (string field, InternetAddressList list)
		{
			var builder = new StringBuilder (" ");
			int lineLength = field.Length + 2;

			list.Encode (builder, ref lineLength);
			builder.Append ('\n');

			var raw = Encoding.ASCII.GetBytes (builder.ToString ());

			Headers.Changed -= HeadersChanged;
			Headers.Replace (new Header (Headers.Options, field, raw));
			Headers.Changed += HeadersChanged;
		}

		void InternetAddressListChanged (object sender, EventArgs e)
		{
			var list = (InternetAddressList) sender;

			foreach (var name in StandardAddressHeaders) {
				if (addresses[name] == list) {
					SerializeAddressList (name, list);
					break;
				}
			}
		}

		void AddAddresses (Header header, InternetAddressList list)
		{
			int length = header.RawValue.Length;
			List<InternetAddress> parsed;
			int index = 0;

			// parse the addresses in the new header and add them to our address list
			if (!InternetAddressList.TryParse (Headers.Options, header.RawValue, ref index, length, false, false, out parsed))
				return;

			list.Changed -= InternetAddressListChanged;
			list.AddRange (parsed);
			list.Changed += InternetAddressListChanged;
		}

		void ReloadAddressList (string field, InternetAddressList list)
		{
			// clear the address list and reload
			list.Changed -= InternetAddressListChanged;
			list.Clear ();

			foreach (var header in Headers) {
				if (icase.Compare (header.Field, field) != 0)
					continue;

				int length = header.RawValue.Length;
				List<InternetAddress> parsed;
				int index = 0;

				if (!InternetAddressList.TryParse (Headers.Options, header.RawValue, ref index, length, false, false, out parsed))
					continue;

				list.AddRange (parsed);
			}

			list.Changed += InternetAddressListChanged;
		}

		void ReloadHeader (HeaderId type, string field)
		{
			if (type == HeaderId.Unknown)
				return;

			if (type == HeaderId.References)
				references.Clear ();
			else if (type == HeaderId.InReplyTo)
				inreplyto.Clear ();

			foreach (var header in Headers) {
				if (icase.Compare (header.Field, field) != 0)
					continue;

				switch (type) {
				case HeaderId.MimeVersion:
					if (MimeUtils.TryParseVersion (header.RawValue, 0, header.RawValue.Length, out version))
						return;
					break;
				case HeaderId.References:
					foreach (var msgid in MimeUtils.EnumerateReferences (header.RawValue, 0, header.RawValue.Length))
						references.Add (msgid);
					break;
				case HeaderId.InReplyTo:
					foreach (var msgid in MimeUtils.EnumerateReferences (header.RawValue, 0, header.RawValue.Length))
						inreplyto.Add (msgid);
					break;
				case HeaderId.MessageId:
					messageId = MimeUtils.EnumerateReferences (header.RawValue, 0, header.RawValue.Length).FirstOrDefault ();
					if (messageId != null)
						return;
					break;
				case HeaderId.Date:
					if (DateUtils.TryParseDateTime (header.RawValue, 0, header.RawValue.Length, out date))
						return;
					break;
				}
			}
		}

		void HeadersChanged (object sender, HeaderListChangedEventArgs e)
		{
			var type = e.Header.Field.ToHeaderId ();
			InternetAddressList list;

			switch (e.Action) {
			case HeaderListChangedAction.Added:
				if (addresses.TryGetValue (e.Header.Field, out list)) {
					AddAddresses (e.Header, list);
					break;
				}

				switch (type) {
				case HeaderId.MimeVersion:
					MimeUtils.TryParseVersion (e.Header.RawValue, 0, e.Header.RawValue.Length, out version);
					break;
				case HeaderId.References:
					foreach (var msgid in MimeUtils.EnumerateReferences (e.Header.RawValue, 0, e.Header.RawValue.Length))
						references.Add (msgid);
					break;
				case HeaderId.InReplyTo:
					foreach (var msgid in MimeUtils.EnumerateReferences (e.Header.RawValue, 0, e.Header.RawValue.Length))
						inreplyto.Add (msgid);
					break;
				case HeaderId.MessageId:
					messageId = MimeUtils.EnumerateReferences (e.Header.RawValue, 0, e.Header.RawValue.Length).FirstOrDefault ();
					break;
				case HeaderId.Date:
					DateUtils.TryParseDateTime (e.Header.RawValue, 0, e.Header.RawValue.Length, out date);
					break;
				}
				break;
			case HeaderListChangedAction.Changed:
			case HeaderListChangedAction.Removed:
				if (addresses.TryGetValue (e.Header.Field, out list)) {
					ReloadAddressList (e.Header.Field, list);
					break;
				}

				ReloadHeader (type, e.Header.Field);
				break;
			case HeaderListChangedAction.Cleared:
				foreach (var kvp in addresses) {
					kvp.Value.Changed -= InternetAddressListChanged;
					kvp.Value.Clear ();
					kvp.Value.Changed += InternetAddressListChanged;
				}

				references.Clear ();
				inreplyto.Clear ();
				messageId = null;
				version = null;
				break;
			default:
				throw new ArgumentOutOfRangeException ();
			}
		}
	}
}
