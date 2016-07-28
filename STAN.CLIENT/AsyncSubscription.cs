﻿/*******************************************************************************
 * Copyright (c) 2015-2016 Apcera Inc. All rights reserved. This program and the accompanying
 * materials are made available under the terms of the MIT License (MIT) which accompanies this
 * distribution, and is available at http://opensource.org/licenses/MIT
 *******************************************************************************/
using System;
using System.Threading;

namespace STAN.Client
{
    class AsyncSubscription : IStanSubscription
    {
        private object mu = new Object();
        private SubscriptionOptions options;
        private string inbox = null;
        private string subject = null;
        private Connection sc = null;
        private string ackInbox = null;
        private NATS.Client.IAsyncSubscription inboxSub = null;
        private EventHandler<StanMsgHandlerArgs> handler;
        private DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);

        ReaderWriterLockSlim rwLock = new ReaderWriterLockSlim();

        internal AsyncSubscription(Connection sc, SubscriptionOptions opts)
        {
            // TODO: Complete member initialization
            this.options = new SubscriptionOptions(opts);
            this.inbox = Connection.newInbox();
            this.sc = sc;
        }

        internal string Inbox
        {
            get { return inbox; }
        }

        internal static long convertTimeSpan(TimeSpan ts)
        {
            return ts.Ticks * 100;
        }

        // in STAN, much of this code is in the connection module.
        internal void subscribe(string subRequestSubject, string subject, string qgroup, EventHandler<StanMsgHandlerArgs> handler)
        {
            Exception exToThrow = null;

            rwLock.EnterWriteLock();

            this.handler += handler;
            this.subject = subject;

            try
            {
                if (sc == null)
                {
                    throw new StanConnectionClosedException();
                }

                // Listen for actual messages.
                inboxSub = sc.NATSConnection.SubscribeAsync(inbox, sc.processMsg);

                SubscriptionRequest sr = new SubscriptionRequest();
                sr.ClientID = sc.ClientID;
                sr.Subject = subject;
                sr.QGroup = (qgroup == null ? "" : qgroup);
                sr.Inbox = inbox;
                sr.MaxInFlight = options.MaxInflight;
                sr.AckWaitInSecs = options.AckWait / 1000;
                sr.StartPosition = options.startAt;
                sr.DurableName = (options.DurableName == null ? "" : options.DurableName);

                // Conditionals
                switch (sr.StartPosition)
                {
                    case StartPosition.TimeDeltaStart:
                        sr.StartTimeDelta = convertTimeSpan(DateTime.Now - options.startTime);
                        break;
                    case StartPosition.SequenceStart:
                        sr.StartSequence = options.startSequence;
                        break;
                }

                byte[] b = ProtocolSerializer.marshal(sr);

                // TODO:  Configure request timeout?
                NATS.Client.Msg m = sc.NATSConnection.Request(subRequestSubject, b, 2000);

                SubscriptionResponse r = new SubscriptionResponse();
                ProtocolSerializer.unmarshal(m.Data, r);

                if (string.IsNullOrWhiteSpace(r.Error) == false)
                {
                    throw new StanException(r.Error);
                }

                ackInbox = r.AckInbox;
            }
            catch (Exception ex)
            {
                if (inboxSub != null)
                {
                    inboxSub.Unsubscribe();
                }
                exToThrow = ex;
            }

            rwLock.ExitWriteLock();

            if (exToThrow != null)
                throw exToThrow;
        }

        public void Unsubscribe()
        {
            string linbox = null;
            Connection lsc = null;

            lock (mu)
            {
                if (sc == null)
                    throw new StanBadSubscriptionException();

                lsc = sc;
                sc = null;

                inboxSub.Unsubscribe();
                linbox = inbox;
            }

            lsc.unsubscribe(subject, ackInbox);
        }

        internal void manualAck(StanMsg m)
        {
            if (m == null)
                return;

            rwLock.EnterReadLock();
            
            string localAckSubject = ackInbox;
            bool   localManualAck = options.manualAcks;
            Connection sc = this.sc;

            rwLock.ExitReadLock();

            if (localManualAck == false)
            {
                throw new StanManualAckException();
            }

            if (sc == null)
            {
                throw new StanBadSubscriptionException();
            }

            byte[] b = ProtocolSerializer.createAck(m.proto);
            sc.NATSConnection.Publish(localAckSubject, b);
        }

        internal void processMsg(MsgProto mp)
        {
            rwLock.EnterReadLock();

            EventHandler<StanMsgHandlerArgs> cb = handler;
            bool isManualAck  = options.manualAcks;
            string localAckSubject = ackInbox;
            IStanConnection subsSc = sc;
            NATS.Client.IConnection localNc = null;

            if (subsSc != null)
            {
                localNc = sc.NATSConnection;
            }

            rwLock.ExitReadLock();

            if (cb != null && subsSc != null)
            {
                StanMsgHandlerArgs args = new StanMsgHandlerArgs(new StanMsg(mp, this));
                cb(this, args);
            }

            if (!isManualAck && localNc != null)
            {
                byte[] b = ProtocolSerializer.createAck(mp);
                try
                {
                    localNc.Publish(localAckSubject, b);
                }
                catch (Exception)
                {
                    /* 
                     * Ignore - subscriber could have closed the connection
                     * or there's been a connection error.  The server will
                     * resend the unacknowledged messages.
                     */
                }
            }
        }

        public void Dispose()
        {
            // Durables must always explicity unsubscribe.
            if (string.IsNullOrEmpty(options.DurableName) == false)
            {
                try
                {
                    Unsubscribe();
                }
                catch (Exception) {  /* ignore */ }
            }
        }

        internal static SubscriptionOptions DefaultOptions
        {
            get { return new SubscriptionOptions(); }
        }
    }
}