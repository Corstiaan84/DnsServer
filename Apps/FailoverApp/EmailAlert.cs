﻿/*
Technitium DNS Server
Copyright (C) 2021  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Mail;

namespace Failover
{
    class EmailAlert : IDisposable
    {
        #region variables

        readonly HealthMonitoringService _service;

        string _name;
        bool _enabled;
        MailAddress[] _alertTo;
        string _smtpServer;
        int _smtpPort;
        bool _startTls;
        bool _smtpOverTls;
        string _username;
        string _password;
        MailAddress _mailFrom;

        readonly SmtpClientEx _smtpClient = new SmtpClientEx();

        #endregion

        #region constructor

        public EmailAlert(HealthMonitoringService service, dynamic jsonEmailAlert)
        {
            _service = service;

            Reload(jsonEmailAlert);
        }

        #endregion

        #region IDisposable

        bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_smtpClient is not null)
                    _smtpClient.Dispose();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region private

        private async Task SendMailAsync(MailMessage message)
        {
            try
            {
                await _smtpClient.SendMailAsync(message);
            }
            catch (Exception ex)
            {
                _service.DnsServer.WriteLog(ex);
            }
        }

        #endregion

        #region public

        public void Reload(dynamic jsonEmailAlert)
        {
            if (jsonEmailAlert.name is null)
                _name = "default";
            else
                _name = jsonEmailAlert.name.Value;

            if (jsonEmailAlert.enabled is null)
                _enabled = false;
            else
                _enabled = jsonEmailAlert.enabled.Value;

            if (jsonEmailAlert.alertTo is null)
            {
                _alertTo = null;
            }
            else
            {
                _alertTo = new MailAddress[jsonEmailAlert.alertTo.Count];

                for (int i = 0; i < _alertTo.Length; i++)
                    _alertTo[i] = new MailAddress(jsonEmailAlert.alertTo[i].Value);
            }

            if (jsonEmailAlert.smtpServer is null)
                _smtpServer = null;
            else
                _smtpServer = jsonEmailAlert.smtpServer.Value;

            if (jsonEmailAlert.smtpPort is null)
                _smtpPort = 25;
            else
                _smtpPort = Convert.ToInt32(jsonEmailAlert.smtpPort.Value);

            if (jsonEmailAlert.startTls is null)
                _startTls = false;
            else
                _startTls = jsonEmailAlert.startTls.Value;

            if (jsonEmailAlert.smtpOverTls is null)
                _smtpOverTls = false;
            else
                _smtpOverTls = jsonEmailAlert.smtpOverTls.Value;

            if (jsonEmailAlert.username is null)
                _username = null;
            else
                _username = jsonEmailAlert.username.Value;

            if (jsonEmailAlert.password is null)
                _password = null;
            else
                _password = jsonEmailAlert.password.Value;

            if (jsonEmailAlert.mailFrom is null)
            {
                _mailFrom = null;
            }
            else
            {
                if (jsonEmailAlert.mailFromName is null)
                    _mailFrom = new MailAddress(jsonEmailAlert.mailFrom.Value);
                else
                    _mailFrom = new MailAddress(jsonEmailAlert.mailFrom.Value, jsonEmailAlert.mailFromName.Value, Encoding.UTF8);
            }

            //update smtp client settings
            _smtpClient.Host = _smtpServer;
            _smtpClient.Port = _smtpPort;
            _smtpClient.EnableSsl = _startTls;
            _smtpClient.EnableSslWrapper = _smtpOverTls;

            if (string.IsNullOrEmpty(_username))
                _smtpClient.Credentials = null;
            else
                _smtpClient.Credentials = new NetworkCredential(_username, _password);

            _smtpClient.Proxy = _service.DnsServer.Proxy;
        }

        public Task SendAlertAsync(IPAddress address, string healthCheck, HealthCheckStatus healthCheckStatus)
        {
            if (!_enabled || (_mailFrom is null) || (_alertTo is null) || (_alertTo.Length == 0))
                return Task.CompletedTask;

            MailMessage message = new MailMessage();

            message.From = _mailFrom;

            foreach (MailAddress alertTo in _alertTo)
                message.To.Add(alertTo);

            if (healthCheckStatus.IsHealthy)
            {
                message.Subject = "[Alert] Address [" + address.ToString() + "] Status Is Healthy";
                message.Body = @"Hi,

The DNS Failover App was successfully able to perform a health check [" + healthCheck + "] on the address [" + address.ToString() + @"] and found that the address was healthy.

Alert time: " + healthCheckStatus.DateTime.ToLongDateString() + @"

Regards,
DNS Failover App
";
            }
            else
            {
                message.Subject = "[Alert] Address [" + address.ToString() + "] Status Is Failed";
                message.Body = @"Hi,

The DNS Failover App was successfully able to perform a health check [" + healthCheck + "] on the address [" + address.ToString() + @"] and found that the address failed to respond. 

The failure reason is: " + healthCheckStatus.FailureReason + @"
Alert time: " + healthCheckStatus.DateTime.ToLongDateString() + @"

Regards,
DNS Failover App
";
            }

            return SendMailAsync(message);
        }

        public Task SendAlertAsync(IPAddress address, string healthCheck, Exception ex)
        {
            if (!_enabled || (_mailFrom is null) || (_alertTo is null) || (_alertTo.Length == 0))
                return Task.CompletedTask;

            MailMessage message = new MailMessage();

            message.From = _mailFrom;

            foreach (MailAddress alertTo in _alertTo)
                message.To.Add(alertTo);

            message.Subject = "[Alert] Address [" + address.ToString() + "] Status Is Error";
            message.Body = @"Hi,

The DNS Failover App has failed to perform a health check [" + healthCheck + "] on the address [" + address.ToString() + @"]. 

The error description is: " + ex.ToString() + @"
Alert time: " + DateTime.UtcNow.ToLongDateString() + @"

Regards,
DNS Failover App
";

            return SendMailAsync(message);
        }

        public Task SendAlertAsync(string domain, DnsResourceRecordType type, string healthCheck, HealthCheckStatus healthCheckStatus)
        {
            if (!_enabled || (_mailFrom is null) || (_alertTo is null) || (_alertTo.Length == 0))
                return Task.CompletedTask;

            MailMessage message = new MailMessage();

            message.From = _mailFrom;

            foreach (MailAddress alertTo in _alertTo)
                message.To.Add(alertTo);

            if (healthCheckStatus.IsHealthy)
            {
                message.Subject = "[Alert] Domain [" + domain + "] Status Is Healthy";
                message.Body = @"Hi,

The DNS Failover App was successfully able to perform a health check [" + healthCheck + "] on the domain name [" + domain + @"] and found that the domain name was healthy.

DNS record type: " + type.ToString() + @"
Alert time: " + healthCheckStatus.DateTime.ToLongDateString() + @"

Regards,
DNS Failover App
";
            }
            else
            {
                message.Subject = "[Alert] Domain [" + domain + "] Status Is Failed";
                message.Body = @"Hi,

The DNS Failover App was successfully able to perform a health check [" + healthCheck + "] on the domain name [" + domain + @"] and found that the domain name failed to respond. 

The failure reason is: " + healthCheckStatus.FailureReason + @"
DNS record type: " + type.ToString() + @"
Alert time: " + healthCheckStatus.DateTime.ToLongDateString() + @"

Regards,
DNS Failover App
";
            }

            return SendMailAsync(message);
        }

        public Task SendAlertAsync(string domain, DnsResourceRecordType type, string healthCheck, Exception ex)
        {
            if (!_enabled || (_mailFrom is null) || (_alertTo is null) || (_alertTo.Length == 0))
                return Task.CompletedTask;

            MailMessage message = new MailMessage();

            message.From = _mailFrom;

            foreach (MailAddress alertTo in _alertTo)
                message.To.Add(alertTo);

            message.Subject = "[Alert] Domain [" + domain + "] Status Is Error";
            message.Body = @"Hi,

The DNS Failover App has failed to perform a health check [" + healthCheck + "] on the domain name [" + domain + @"]. 

The error description is: " + ex.ToString() + @"
DNS record type: " + type.ToString() + @"
Alert time: " + DateTime.UtcNow.ToLongDateString() + @"

Regards,
DNS Failover App
";

            return SendMailAsync(message);
        }

        #endregion

        #region properties

        public string Name
        { get { return _name; } }

        public bool Enabled
        { get { return _enabled; } }

        public IReadOnlyList<MailAddress> AlertTo
        { get { return _alertTo; } }

        public string SmtpServer
        { get { return _smtpServer; } }

        public int SmtpPort
        { get { return _smtpPort; } }

        public bool StartTls
        { get { return _startTls; } }

        public bool SmtpOverTls
        { get { return _smtpOverTls; } }

        public string Username
        { get { return _username; } }

        public string Password
        { get { return _password; } }

        public MailAddress MailFrom
        { get { return _mailFrom; } }

        #endregion
    }
}