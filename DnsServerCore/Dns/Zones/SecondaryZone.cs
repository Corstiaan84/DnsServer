﻿/*
Technitium DNS Server
Copyright (C) 2023  Shreyas Zare (shreyas@technitium.com)

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

using DnsServerCore.Dns.ResourceRecords;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TechnitiumLibrary;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace DnsServerCore.Dns.Zones
{
    class SecondaryZone : ApexZone
    {
        #region variables

        readonly DnsServer _dnsServer;

        readonly object _refreshTimerLock = new object();
        Timer _refreshTimer;
        bool _refreshTimerTriggered;
        const int REFRESH_TIMER_INTERVAL = 5000;

        const int REFRESH_SOA_TIMEOUT = 10000;
        const int REFRESH_XFR_TIMEOUT = 120000;
        const int REFRESH_RETRIES = 5;

        const int REFRESH_TSIG_FUDGE = 300;

        DateTime _expiry;
        bool _isExpired;

        bool _resync;

        #endregion

        #region constructor

        public SecondaryZone(DnsServer dnsServer, AuthZoneInfo zoneInfo)
            : base(zoneInfo)
        {
            _dnsServer = dnsServer;

            _expiry = zoneInfo.Expiry;

            _isExpired = DateTime.UtcNow > _expiry;
            _refreshTimer = new Timer(RefreshTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            InitNotify(_dnsServer);
        }

        private SecondaryZone(DnsServer dnsServer, string name)
            : base(name)
        {
            _dnsServer = dnsServer;

            _zoneTransfer = AuthZoneTransfer.Deny;
            _notify = AuthZoneNotify.None;
            _update = AuthZoneUpdate.Deny;

            InitNotify(_dnsServer);
        }

        #endregion

        #region static

        public static async Task<SecondaryZone> CreateAsync(DnsServer dnsServer, string name, string primaryNameServerAddresses = null, DnsTransportProtocol zoneTransferProtocol = DnsTransportProtocol.Tcp, string tsigKeyName = null)
        {
            switch (zoneTransferProtocol)
            {
                case DnsTransportProtocol.Tcp:
                case DnsTransportProtocol.Tls:
                case DnsTransportProtocol.Quic:
                    break;

                default:
                    throw new NotSupportedException("Zone transfer protocol is not supported: XFR-over-" + zoneTransferProtocol.ToString().ToUpper());
            }

            SecondaryZone secondaryZone = new SecondaryZone(dnsServer, name);

            DnsQuestionRecord soaQuestion = new DnsQuestionRecord(name, DnsResourceRecordType.SOA, DnsClass.IN);
            DnsDatagram soaResponse;
            NameServerAddress[] primaryNameServers = null;

            try
            {
                if (string.IsNullOrEmpty(primaryNameServerAddresses))
                {
                    soaResponse = await secondaryZone._dnsServer.DirectQueryAsync(soaQuestion);
                }
                else
                {
                    primaryNameServers = primaryNameServerAddresses.Split(delegate (string address)
                    {
                        NameServerAddress nameServer = NameServerAddress.Parse(address);

                        if (nameServer.Protocol != zoneTransferProtocol)
                            nameServer = nameServer.ChangeProtocol(zoneTransferProtocol);

                        return nameServer;
                    }, ',');

                    DnsClient dnsClient = new DnsClient(primaryNameServers);

                    foreach (NameServerAddress nameServerAddress in dnsClient.Servers)
                    {
                        if (nameServerAddress.IsIPEndPointStale)
                            await nameServerAddress.ResolveIPAddressAsync(secondaryZone._dnsServer, secondaryZone._dnsServer.PreferIPv6);
                    }

                    dnsClient.Proxy = secondaryZone._dnsServer.Proxy;
                    dnsClient.PreferIPv6 = secondaryZone._dnsServer.PreferIPv6;

                    DnsDatagram soaRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { soaQuestion }, null, null, null, dnsServer.UdpPayloadSize);

                    if (string.IsNullOrEmpty(tsigKeyName))
                        soaResponse = await dnsClient.ResolveAsync(soaRequest);
                    else if ((dnsServer.TsigKeys is not null) && dnsServer.TsigKeys.TryGetValue(tsigKeyName, out TsigKey key))
                        soaResponse = await dnsClient.ResolveAsync(soaRequest, key, REFRESH_TSIG_FUDGE);
                    else
                        throw new DnsServerException("No such TSIG key was found configured: " + tsigKeyName);
                }
            }
            catch (Exception ex)
            {
                throw new DnsServerException("DNS Server failed to find SOA record for: " + name, ex);
            }

            if ((soaResponse.Answer.Count == 0) || (soaResponse.Answer[0].Type != DnsResourceRecordType.SOA))
                throw new DnsServerException("DNS Server failed to find SOA record for: " + name);

            DnsSOARecordData receivedSoa = soaResponse.Answer[0].RDATA as DnsSOARecordData;

            DnsSOARecordData soa = new DnsSOARecordData(receivedSoa.PrimaryNameServer, receivedSoa.ResponsiblePerson, 0u, receivedSoa.Refresh, receivedSoa.Retry, receivedSoa.Expire, receivedSoa.Minimum);
            DnsResourceRecord[] soaRR = new DnsResourceRecord[] { new DnsResourceRecord(secondaryZone._name, DnsResourceRecordType.SOA, DnsClass.IN, soa.Refresh, soa) };

            AuthRecordInfo authRecordInfo = soaRR[0].GetAuthRecordInfo();

            authRecordInfo.PrimaryNameServers = primaryNameServers;
            authRecordInfo.ZoneTransferProtocol = zoneTransferProtocol;
            authRecordInfo.TsigKeyName = tsigKeyName;

            secondaryZone._entries[DnsResourceRecordType.SOA] = soaRR;

            secondaryZone._isExpired = true; //new secondary zone is considered expired till it refreshes
            secondaryZone._refreshTimer = new Timer(secondaryZone.RefreshTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            return secondaryZone;
        }

        #endregion

        #region IDisposable

        bool _disposed;

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (_disposed)
                    return;

                if (disposing)
                {
                    lock (_refreshTimerLock)
                    {
                        if (_refreshTimer != null)
                        {
                            _refreshTimer.Dispose();
                            _refreshTimer = null;
                        }
                    }
                }

                _disposed = true;
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        #endregion

        #region private

        private async void RefreshTimerCallback(object state)
        {
            try
            {
                if (_disabled && !_resync)
                    return;

                _isExpired = DateTime.UtcNow > _expiry;

                //get primary name server addresses
                IReadOnlyList<NameServerAddress> primaryNameServers = await GetPrimaryNameServerAddressesAsync(_dnsServer);

                DnsResourceRecord currentSoaRecord = _entries[DnsResourceRecordType.SOA][0];
                DnsSOARecordData currentSoa = currentSoaRecord.RDATA as DnsSOARecordData;

                if (primaryNameServers.Count == 0)
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("DNS Server could not find primary name server IP addresses for secondary zone: " + (_name == "" ? "<root>" : _name));

                    //set timer for retry
                    ResetRefreshTimer(currentSoa.Retry * 1000);
                    _syncFailed = true;
                    return;
                }

                AuthRecordInfo recordInfo = currentSoaRecord.GetAuthRecordInfo();
                TsigKey key = null;

                if (!string.IsNullOrEmpty(recordInfo.TsigKeyName) && ((_dnsServer.TsigKeys is null) || !_dnsServer.TsigKeys.TryGetValue(recordInfo.TsigKeyName, out key)))
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("DNS Server does not have TSIG key '" + recordInfo.TsigKeyName + "' configured for refreshing secondary zone: " + (_name == "" ? "<root>" : _name));

                    //set timer for retry
                    ResetRefreshTimer(currentSoa.Retry * 1000);
                    _syncFailed = true;
                    return;
                }

                //refresh zone
                if (await RefreshZoneAsync(primaryNameServers, recordInfo.ZoneTransferProtocol, key))
                {
                    //zone refreshed; set timer for refresh
                    DnsSOARecordData latestSoa = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                    ResetRefreshTimer(latestSoa.Refresh * 1000);
                    _syncFailed = false;
                    _expiry = DateTime.UtcNow.AddSeconds(latestSoa.Expire);
                    _isExpired = false;
                    _resync = false;
                    _dnsServer.AuthZoneManager.SaveZoneFile(_name);
                    return;
                }

                //no response from any of the name servers; set timer for retry
                DnsSOARecordData soa = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                ResetRefreshTimer(soa.Retry * 1000);
                _syncFailed = true;
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log != null)
                    log.Write(ex);

                //set timer for retry
                DnsSOARecordData soa = _entries[DnsResourceRecordType.SOA][0].RDATA as DnsSOARecordData;
                ResetRefreshTimer(soa.Retry * 1000);
                _syncFailed = true;
            }
            finally
            {
                _refreshTimerTriggered = false;
            }
        }

        private void ResetRefreshTimer(long dueTime)
        {
            lock (_refreshTimerLock)
            {
                if (_refreshTimer != null)
                    _refreshTimer.Change(dueTime, Timeout.Infinite);
            }
        }

        private async Task<bool> RefreshZoneAsync(IReadOnlyList<NameServerAddress> primaryNameServers, DnsTransportProtocol zoneTransferProtocol, TsigKey key)
        {
            try
            {
                {
                    LogManager log = _dnsServer.LogManager;
                    if (log != null)
                        log.Write("DNS Server has started zone refresh for secondary zone: " + (_name == "" ? "<root>" : _name));
                }

                DnsResourceRecord currentSoaRecord = _entries[DnsResourceRecordType.SOA][0];
                DnsSOARecordData currentSoa = currentSoaRecord.RDATA as DnsSOARecordData;

                if (!_resync)
                {
                    //check for update; use UDP transport
                    List<NameServerAddress> udpNameServers = new List<NameServerAddress>(primaryNameServers.Count);

                    foreach (NameServerAddress primaryNameServer in primaryNameServers)
                    {
                        if (primaryNameServer.Protocol == DnsTransportProtocol.Udp)
                            udpNameServers.Add(primaryNameServer);
                        else
                            udpNameServers.Add(primaryNameServer.ChangeProtocol(DnsTransportProtocol.Udp));
                    }

                    DnsClient client = new DnsClient(udpNameServers);

                    client.Proxy = _dnsServer.Proxy;
                    client.PreferIPv6 = _dnsServer.PreferIPv6;
                    client.Timeout = REFRESH_SOA_TIMEOUT;
                    client.Retries = REFRESH_RETRIES;
                    client.Concurrency = 1;

                    DnsDatagram soaRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { new DnsQuestionRecord(_name, DnsResourceRecordType.SOA, DnsClass.IN) }, null, null, null, _dnsServer.UdpPayloadSize);
                    DnsDatagram soaResponse;

                    if (key is null)
                        soaResponse = await client.ResolveAsync(soaRequest);
                    else
                        soaResponse = await client.ResolveAsync(soaRequest, key, REFRESH_TSIG_FUDGE);

                    if (soaResponse.RCODE != DnsResponseCode.NoError)
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server received RCODE=" + soaResponse.RCODE.ToString() + " for '" + (_name == "" ? "<root>" : _name) + "' secondary zone refresh from: " + soaResponse.Metadata.NameServer.ToString());

                        return false;
                    }

                    if ((soaResponse.Answer.Count < 1) || (soaResponse.Answer[0].Type != DnsResourceRecordType.SOA) || !_name.Equals(soaResponse.Answer[0].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server received an empty response for SOA query for '" + (_name == "" ? "<root>" : _name) + "' secondary zone refresh from: " + soaResponse.Metadata.NameServer.ToString());

                        return false;
                    }

                    DnsResourceRecord receivedSoaRecord = soaResponse.Answer[0];
                    DnsSOARecordData receivedSoa = receivedSoaRecord.RDATA as DnsSOARecordData;

                    //compare using sequence space arithmetic
                    if (!currentSoa.IsZoneUpdateAvailable(receivedSoa))
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server successfully checked for '" + (_name == "" ? "<root>" : _name) + "' secondary zone update from: " + soaResponse.Metadata.NameServer.ToString());

                        return true;
                    }
                }

                //update available; do zone transfer with TLS, QUIC, or TCP transport
                List<NameServerAddress> updatedNameServers = new List<NameServerAddress>(primaryNameServers.Count);

                switch (zoneTransferProtocol)
                {
                    case DnsTransportProtocol.Tls:
                    case DnsTransportProtocol.Quic:
                        //change name server protocol to TLS/QUIC
                        foreach (NameServerAddress primaryNameServer in primaryNameServers)
                        {
                            if (primaryNameServer.Protocol == zoneTransferProtocol)
                                updatedNameServers.Add(primaryNameServer);
                            else
                                updatedNameServers.Add(primaryNameServer.ChangeProtocol(zoneTransferProtocol));
                        }

                        break;

                    default:
                        //change name server protocol to TCP
                        foreach (NameServerAddress primaryNameServer in primaryNameServers)
                        {
                            if (primaryNameServer.Protocol == DnsTransportProtocol.Tcp)
                                updatedNameServers.Add(primaryNameServer);
                            else
                                updatedNameServers.Add(primaryNameServer.ChangeProtocol(DnsTransportProtocol.Tcp));
                        }

                        break;
                }

                DnsClient xfrClient = new DnsClient(updatedNameServers);

                xfrClient.Proxy = _dnsServer.Proxy;
                xfrClient.PreferIPv6 = _dnsServer.PreferIPv6;
                xfrClient.Timeout = REFRESH_XFR_TIMEOUT;
                xfrClient.Retries = REFRESH_RETRIES;
                xfrClient.Concurrency = 1;

                bool doIXFR = !_isExpired && !_resync;

                while (true)
                {
                    DnsQuestionRecord xfrQuestion;
                    IReadOnlyList<DnsResourceRecord> xfrAuthority;

                    if (doIXFR)
                    {
                        xfrQuestion = new DnsQuestionRecord(_name, DnsResourceRecordType.IXFR, DnsClass.IN);
                        xfrAuthority = new DnsResourceRecord[] { currentSoaRecord };
                    }
                    else
                    {
                        xfrQuestion = new DnsQuestionRecord(_name, DnsResourceRecordType.AXFR, DnsClass.IN);
                        xfrAuthority = null;
                    }

                    DnsDatagram xfrRequest = new DnsDatagram(0, false, DnsOpcode.StandardQuery, false, false, false, false, false, false, DnsResponseCode.NoError, new DnsQuestionRecord[] { xfrQuestion }, null, xfrAuthority);
                    DnsDatagram xfrResponse;

                    if (key is null)
                        xfrResponse = await xfrClient.ResolveAsync(xfrRequest);
                    else
                        xfrResponse = await xfrClient.ResolveAsync(xfrRequest, key, REFRESH_TSIG_FUDGE);

                    if (doIXFR && (xfrResponse.RCODE == DnsResponseCode.NotImplemented))
                    {
                        doIXFR = false;
                        continue;
                    }

                    if (xfrResponse.RCODE != DnsResponseCode.NoError)
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server received a zone transfer response (RCODE=" + xfrResponse.RCODE.ToString() + ") for '" + (_name == "" ? "<root>" : _name) + "' secondary zone from: " + xfrResponse.Metadata.NameServer.ToString());

                        return false;
                    }

                    if (xfrResponse.Answer.Count < 1)
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server received an empty response for zone transfer query for '" + (_name == "" ? "<root>" : _name) + "' secondary zone from: " + xfrResponse.Metadata.NameServer.ToString());

                        return false;
                    }

                    if (!_name.Equals(xfrResponse.Answer[0].Name, StringComparison.OrdinalIgnoreCase) || (xfrResponse.Answer[0].Type != DnsResourceRecordType.SOA) || (xfrResponse.Answer[0].RDATA is not DnsSOARecordData xfrSoa))
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server received invalid response for zone transfer query for '" + (_name == "" ? "<root>" : _name) + "' secondary zone from: " + xfrResponse.Metadata.NameServer.ToString());

                        return false;
                    }

                    if (_resync || currentSoa.IsZoneUpdateAvailable(xfrSoa))
                    {
                        xfrResponse = xfrResponse.Join(); //join multi message response

                        if (doIXFR)
                        {
                            IReadOnlyList<DnsResourceRecord> historyRecords = _dnsServer.AuthZoneManager.SyncIncrementalZoneTransferRecords(_name, xfrResponse.Answer);
                            if (historyRecords.Count > 0)
                                CommitZoneHistory(historyRecords);
                            else
                                ClearZoneHistory(); //AXFR response was received
                        }
                        else
                        {
                            _dnsServer.AuthZoneManager.SyncZoneTransferRecords(_name, xfrResponse.Answer);
                            ClearZoneHistory();
                        }

                        _lastModified = DateTime.UtcNow;

                        //trigger notify
                        TriggerNotify();

                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server successfully refreshed '" + (_name == "" ? "<root>" : _name) + "' secondary zone from: " + xfrResponse.Metadata.NameServer.ToString());
                    }
                    else
                    {
                        LogManager log = _dnsServer.LogManager;
                        if (log != null)
                            log.Write("DNS Server successfully checked for '" + (_name == "" ? "<root>" : _name) + "' secondary zone update from: " + xfrResponse.Metadata.NameServer.ToString());
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                LogManager log = _dnsServer.LogManager;
                if (log != null)
                {
                    string strNameServers = null;

                    foreach (NameServerAddress nameServer in primaryNameServers)
                    {
                        if (strNameServers == null)
                            strNameServers = nameServer.ToString();
                        else
                            strNameServers += ", " + nameServer.ToString();
                    }

                    log.Write("DNS Server failed to refresh '" + (_name == "" ? "<root>" : _name) + "' secondary zone from: " + strNameServers + "\r\n" + ex.ToString());
                }

                return false;
            }
        }

        private void CommitZoneHistory(IReadOnlyList<DnsResourceRecord> historyRecords)
        {
            lock (_zoneHistory)
            {
                historyRecords[0].GetAuthRecordInfo().DeletedOn = DateTime.UtcNow;

                //write history
                _zoneHistory.AddRange(historyRecords);

                CleanupHistory(_zoneHistory);
            }
        }

        private void ClearZoneHistory()
        {
            lock (_zoneHistory)
            {
                _zoneHistory.Clear();
            }
        }

        #endregion

        #region public

        public void TriggerRefresh(int refreshInterval = REFRESH_TIMER_INTERVAL)
        {
            if (_disabled)
                return;

            if (_refreshTimerTriggered)
                return;

            _refreshTimerTriggered = true;
            ResetRefreshTimer(refreshInterval);
        }

        public void TriggerResync()
        {
            if (_refreshTimerTriggered)
                return;

            _resync = true;

            _refreshTimerTriggered = true;
            ResetRefreshTimer(0);
        }

        public override void SetRecords(DnsResourceRecordType type, IReadOnlyList<DnsResourceRecord> records)
        {
            switch (type)
            {
                case DnsResourceRecordType.SOA:
                    if ((records.Count != 1) || !records[0].Name.Equals(_name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Invalid SOA record.");

                    DnsResourceRecord existingSoaRecord = _entries[DnsResourceRecordType.SOA][0];
                    DnsResourceRecord newSoaRecord = records[0];

                    existingSoaRecord.CopyRecordInfoFrom(newSoaRecord);
                    break;

                default:
                    throw new InvalidOperationException("Cannot set records in secondary zone.");
            }
        }

        public override void AddRecord(DnsResourceRecord record)
        {
            throw new InvalidOperationException("Cannot add record in secondary zone.");
        }

        public override bool DeleteRecord(DnsResourceRecordType type, DnsResourceRecordData record)
        {
            throw new InvalidOperationException("Cannot delete record in secondary zone.");
        }

        public override bool DeleteRecords(DnsResourceRecordType type)
        {
            throw new InvalidOperationException("Cannot delete records in secondary zone.");
        }

        public override void UpdateRecord(DnsResourceRecord oldRecord, DnsResourceRecord newRecord)
        {
            throw new InvalidOperationException("Cannot update record in secondary zone.");
        }

        #endregion

        #region properties

        public override AuthZoneUpdate Update
        {
            get { return _update; }
            set { throw new InvalidOperationException(); }
        }

        public DateTime Expiry
        { get { return _expiry; } }

        public bool IsExpired
        { get { return _isExpired; } }

        public override bool Disabled
        {
            get { return _disabled; }
            set
            {
                if (_disabled != value)
                {
                    _disabled = value;

                    if (_disabled)
                    {
                        DisableNotifyTimer();
                        ResetRefreshTimer(Timeout.Infinite);
                    }
                    else
                    {
                        TriggerNotify();
                        TriggerRefresh();
                    }
                }
            }
        }

        public override bool IsActive
        {
            get { return !_disabled && !_isExpired; }
        }

        #endregion
    }
}
