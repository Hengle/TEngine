﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ProtoBuf;
using ProtoBuf.Meta;

namespace TEngine.Runtime
{
    public class NetworkChannelHelper : INetworkChannelHelper
    {
        private readonly Dictionary<int, Type> m_ServerToClientPacketTypes = new Dictionary<int, Type>();
        private readonly MemoryStream m_CachedStream = new MemoryStream(1024 * 8);
        private INetworkChannel m_NetworkChannel = null;

        /// <summary>
        /// 获取消息包头长度。
        /// </summary>
        public int PacketHeaderLength => sizeof(int);

        /// <summary>
        /// 初始化网络频道辅助器。
        /// </summary>
        /// <param name="networkChannel">网络频道。</param>
        public void Initialize(INetworkChannel networkChannel)
        {
            m_NetworkChannel = networkChannel;

            // 反射注册包和包处理函数。
            Type packetBaseType = typeof(CSPacketBase);
            Type packetHandlerBaseType = typeof(PacketHandlerBase);
            Assembly assembly = Assembly.GetExecutingAssembly();
            Type[] types = assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                if (!types[i].IsClass || types[i].IsAbstract)
                {
                    continue;
                }

                if (types[i].BaseType == packetBaseType)
                {
                    PacketBase packetBase = (PacketBase)Activator.CreateInstance(types[i]);
                    Type packetType = GetServerToClientPacketType(packetBase.Id);
                    if (packetType != null)
                    {
                        Log.Warning("Already exist packet type '{0}', check '{1}' or '{2}'?.", packetBase.Id.ToString(), packetType.Name, packetBase.GetType().Name);
                        continue;
                    }

                    m_ServerToClientPacketTypes.Add(packetBase.Id, types[i]);
                }
                else if (types[i].BaseType == packetHandlerBaseType)
                {
                    IPacketHandler packetHandler = (IPacketHandler)Activator.CreateInstance(types[i]);
                    m_NetworkChannel.RegisterHandler(packetHandler);
                }
            }

            NetEvent.Instance.Subscribe(NetworkConnectedEvent.EventId, OnNetworkConnected);
            NetEvent.Instance.Subscribe(NetworkClosedEvent.EventId, OnNetworkClosed);
            NetEvent.Instance.Subscribe(NetworkMissHeartBeatEvent.EventId, OnNetworkMissHeartBeat);
            NetEvent.Instance.Subscribe(NetworkErrorEvent.EventId, OnNetworkError);
            NetEvent.Instance.Subscribe(NetworkCustomErrorEvent.EventId, OnNetworkCustomError);
        }

        /// <summary>
        /// 准备进行连接。
        /// </summary>
        public void PrepareForConnecting()
        {
            m_NetworkChannel.Socket.ReceiveBufferSize = 1024 * 64;
            m_NetworkChannel.Socket.SendBufferSize = 1024 * 64;
        }

        /// <summary>
        /// 发送心跳消息包。
        /// </summary>
        /// <returns>是否发送心跳消息包成功。</returns>
        public bool SendHeartBeat()
        {
            m_NetworkChannel.Send(MemoryPool.Acquire<CSHeartBeat>());
            return true;
        }

        /// <summary>
        /// 序列化消息包。
        /// </summary>
        /// <typeparam name="T">消息包类型。</typeparam>
        /// <param name="packet">要序列化的消息包。</param>
        /// <param name="destination">要序列化的目标流。</param>
        /// <returns>是否序列化成功。</returns>
        public bool Serialize<T>(T packet, Stream destination) where T : Packet
        {
            PacketBase packetImpl = packet as PacketBase;
            if (packetImpl == null)
            {
                Log.Warning("Packet is invalid.");
                return false;
            }

            m_CachedStream.SetLength(m_CachedStream.Capacity); // 此行防止 Array.Copy 的数据无法写入
            m_CachedStream.Position = 0L;

            CSPacketHeader packetHeader = MemoryPool.Acquire<CSPacketHeader>();
            Serializer.Serialize(m_CachedStream, packetHeader);
            MemoryPool.Release(packetHeader);

            Serializer.SerializeWithLengthPrefix(m_CachedStream, packet, PrefixStyle.Fixed32);
            MemoryPool.Release((IMemory)packet);

            m_CachedStream.WriteTo(destination);
            return true;
        }

        /// <summary>
        /// 反序列化消息包。
        /// </summary>
        /// <param name="packetHeader">消息包头。</param>
        /// <param name="source">要反序列化的来源流。</param>
        /// <param name="customErrorData">用户自定义错误数据。</param>
        /// <returns>反序列化后的消息包。</returns>
        public Packet DeserializePacket(IPacketHeader packetHeader, Stream source, out object customErrorData)
        {
            // 注意：此函数并不在主线程调用！
            customErrorData = null;

            CSPacketHeader csPacketHeader = packetHeader as CSPacketHeader;
            if (csPacketHeader == null)
            {
                Log.Warning("Packet header is invalid.");
                return null;
            }

            Packet packet = null;
            if (csPacketHeader.IsValid)
            {
                Type packetType = GetServerToClientPacketType(csPacketHeader.Id);
                if (packetType != null)
                {
                    packet = (Packet)RuntimeTypeModel.Default.DeserializeWithLengthPrefix(source, MemoryPool.Acquire(packetType), packetType, PrefixStyle.Fixed32, 0);
                }
                else
                {
                    Log.Warning("Can not deserialize packet for packet id '{0}'.", csPacketHeader.Id.ToString());
                }
            }
            else
            {
                Log.Warning("Packet header is invalid.");
            }

            MemoryPool.Release(csPacketHeader);
            return packet;
        }

        public IPacketHeader DeserializePacketHeader(Stream source, out object customErrorData)
        {
            // 注意：此函数并不在主线程调用！
            customErrorData = null;
            return (IPacketHeader)RuntimeTypeModel.Default.Deserialize(source, MemoryPool.Acquire<CSPacketHeader>(), typeof(CSPacketHeader));
        }

        private Type GetServerToClientPacketType(int id)
        {
            Type type = null;
            if (m_ServerToClientPacketTypes.TryGetValue(id, out type))
            {
                return type;
            }
            return null;
        }

        /// <summary>
        /// 关闭并清理网络频道辅助器。
        /// </summary>
        public void Shutdown()
        {
            NetEvent.Instance.Unsubscribe(NetworkConnectedEvent.EventId, OnNetworkConnected);
            NetEvent.Instance.Unsubscribe(NetworkClosedEvent.EventId, OnNetworkClosed);
            NetEvent.Instance.Unsubscribe(NetworkMissHeartBeatEvent.EventId, OnNetworkMissHeartBeat);
            NetEvent.Instance.Unsubscribe(NetworkErrorEvent.EventId, OnNetworkError);
            NetEvent.Instance.Unsubscribe(NetworkCustomErrorEvent.EventId, OnNetworkCustomError);

            m_NetworkChannel = null;
        }

        #region Handle
        private void OnNetworkConnected(object sender, GameEventArgs e)
        {
            NetworkConnectedEvent ne = (NetworkConnectedEvent)e;
            if (ne.NetworkChannel != m_NetworkChannel)
            {
                return;
            }

            if (ne.NetworkChannel.Socket == null)
            {
                return;
            }
            Log.Info("Network channel '{0}' connected, local address '{1}', remote address '{2}'.", ne.NetworkChannel.Name, ne.NetworkChannel.Socket.LocalEndPoint.ToString(), ne.NetworkChannel.Socket.RemoteEndPoint.ToString());
        }

        private void OnNetworkClosed(object sender, GameEventArgs e)
        {
            NetworkClosedEvent ne = (NetworkClosedEvent)e;
            if (ne.NetworkChannel != m_NetworkChannel)
            {
                return;
            }
            Log.Warning("Network channel '{0}' closed.", ne.NetworkChannel.Name);
        }

        private void OnNetworkMissHeartBeat(object sender, GameEventArgs e)
        {
            NetworkMissHeartBeatEvent ne = (NetworkMissHeartBeatEvent)e;
            if (ne.NetworkChannel != m_NetworkChannel)
            {
                return;
            }

            Log.Warning("Network channel '{0}' miss heart beat '{1}' times.", ne.NetworkChannel.Name, ne.MissCount.ToString());

            if (ne.MissCount < 2)
            {
                return;
            }
            ne.NetworkChannel.Close();
        }

        private void OnNetworkError(object sender, GameEventArgs e)
        {
            NetworkErrorEvent ne = (NetworkErrorEvent)e;
            if (ne.NetworkChannel != m_NetworkChannel)
            {
                return;
            }

            Log.Fatal("Network channel '{0}' error, error code is '{1}', error message is '{2}'.", ne.NetworkChannel.Name, ne.ErrorCode.ToString(), ne.ErrorMessage);

            ne.NetworkChannel.Close();
        }

        private void OnNetworkCustomError(object sender, GameEventArgs e)
        {
            NetworkCustomErrorEvent ne = (NetworkCustomErrorEvent)e;
            if (ne.NetworkChannel != m_NetworkChannel)
            {
                return;
            }
            
            Log.Fatal("Network channel '{0}' error, error code is '{1}', error message is '{2}'.", ne.NetworkChannel.Name, ne.CustomErrorData.ToString());
        }
        #endregion
    }
}