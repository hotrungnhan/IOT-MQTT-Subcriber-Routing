// Copyright (c) Atlas Lift Tech Inc. All rights reserved.

#nullable enable

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MQTTnet.AspNetCore.Client.Routing.Attribute;
using MQTTnet.AspNetCore.Client.Routing.Interface;
using MQTTnet.Client;

namespace MQTTnet.AspNetCore.Client.Routing.Lib
{
    public class MqttRouter
    {
        private readonly ILogger<MqttRouter> logger;
        private readonly MqttRouteTable routeTable;
        private readonly ITypeActivatorCache typeActivator;

        public MqttRouter(ILogger<MqttRouter> logger, MqttRouteTable routeTable, ITypeActivatorCache typeActivator)
        {
            this.logger = logger;
            this.routeTable = routeTable;
            this.typeActivator = typeActivator;
        }

        internal async Task OnMessageReceiveEvent(IServiceProvider svcProvider,
            MqttApplicationMessageReceivedEventArgs context, bool allowUnmatchedRoutes)
        {
            // Don't process messages sent from the server itself. This avoids footguns like a server failing to publish
            // a message because a route isn't found on a controller.

            // if (context.ClientId == null)
            // {
            //     return;
            // }

            var routeContext = new MqttRouteContext(context.ApplicationMessage.Topic);

            routeTable.Route(routeContext);

            if (routeContext.Handler == null)
            {
                // Route not found
                if (!allowUnmatchedRoutes)
                {
                    logger.LogDebug(
                        $"Rejecting message publish because '{context.ApplicationMessage.Topic}' did not match any known routes.");
                }

                // context.ProcessPublish = allowUnmatchedRoutes;
            }
            else
            {
                using (var scope = svcProvider.CreateScope())
                {
                    Type? declaringType = routeContext.Handler.DeclaringType;

                    if (declaringType == null)
                    {
                        throw new InvalidOperationException($"{routeContext.Handler} must have a declaring type.");
                    }

                    var classInstance = typeActivator.CreateInstance<object>(scope.ServiceProvider, declaringType);

                    // Potential perf improvement is to cache this reflection work in the future.
                    var activateProperties = declaringType.GetRuntimeProperties()
                        .Where((property) =>
                        {
                            return
                                // property.IsDefined(typeof(MqttControllerContextAttribute)) &&
                                property.GetIndexParameters().Length == 0 &&
                                property.SetMethod != null &&
                                !property.SetMethod.IsStatic;
                        })
                        .ToArray();


                    // if (activateProperties.Length == 0)
                    // {
                    //     logger.LogDebug(
                    //         $"MqttController '{declaringType.FullName}' does not have a property that can accept a controller context.  You may want to add a [{nameof(MqttControllerContextAttribute)}] to a pubilc property.");
                    // }

                    // var controllerContext = new MqttControllerContext()
                    // {
                    //     MqttContext = context,
                    //     MqttServer = scope.ServiceProvider.GetRequiredService<MqttServer>()
                    // };
                    //
                    // for (int i = 0; i < activateProperties.Length; i++)
                    // {
                    //     PropertyInfo property = activateProperties[i];
                    //     property.SetValue(classInstance, controllerContext);
                    // }

                    if (routeContext.HaveControllerParameter)
                    {
                        var tmpx = routeContext.ControllerTemplate;
                        tmpx.Segments.Where(p => p.IsParameter).ToList().ForEach(ts =>
                        {
                            var pro = declaringType.GetRuntimeProperty(ts.Value);
                            if (pro != null)
                            {
                                if (routeContext.Parameters.TryGetValue(ts.Value, out object pvalue))
                                {
                                    pro.SetValue(classInstance, pvalue);
                                }
                            }
                        });
                    }

                    ParameterInfo[] parameters = routeContext.Handler.GetParameters();

                    // context.ProcessPublish = true;

                    if (parameters.Length == 0)
                    {
                        await HandlerInvoker(routeContext.Handler, classInstance, null).ConfigureAwait(false);
                    }
                    else
                    {
                        object?[] paramArray;

                        try
                        {
                            paramArray = parameters.Select(p =>
                                    MatchParameterOrThrow(p, routeContext.Parameters,
                                        // controllerContext, 
                                        svcProvider)
                                )
                                .ToArray();

                            await HandlerInvoker(routeContext.Handler, classInstance, paramArray).ConfigureAwait(false);
                        }
                        catch (ArgumentException ex)
                        {
                            logger.LogError(ex,
                                $"Unable to match route parameters to all arguments. See inner exception for details.");

                            // context.ProcessPublish = false;
                        }
                        catch (TargetInvocationException ex)
                        {
                            logger.LogError(ex.InnerException,
                                $"Unhandled MQTT action exception. See inner exception for details.");

                            // This is an unandled exception from the invoked action
                            // context.ProcessPublish = false;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Unable to invoke Mqtt Action.  See inner exception for details.");

                            // context.ProcessPublish = false;
                        }
                    }
                }
            }
        }

        private static Task HandlerInvoker(MethodInfo method, object instance, object?[]? parameters)
        {
            if (method.ReturnType == typeof(void))
            {
                method.Invoke(instance, parameters);

                return Task.CompletedTask;
            }
            else if (method.ReturnType == typeof(Task))
            {
                var result = (Task?)method.Invoke(instance, parameters);

                if (result == null)
                {
                    throw new NullReferenceException(
                        $"{method.DeclaringType.FullName}.{method.Name} returned null instead of Task");
                }

                return result;
            }

            throw new InvalidOperationException(
                $"Unsupported Action return type \"{method.ReturnType}\" on method {method.DeclaringType.FullName}.{method.Name}. Only void and {nameof(Task)} are allowed.");
        }

        private static object? MatchParameterOrThrow(ParameterInfo param,
            IReadOnlyDictionary<string, object> availableParmeters,
            // MqttControllerContext controllerContext,
            IServiceProvider serviceProvider)
        {
            if (param.IsDefined(typeof(FromPayloadAttribute), false))
            {
                // JsonSerializerOptions? defaultOptions =
                //     serviceProvider.GetService<MqttDefaultJsonOptions>()?.SerializerOptions;
                // return JsonSerializer.Deserialize(controllerContext.MqttContext.ApplicationMessage.Payload,
                //     param.ParameterType,
                //     defaultOptions
                // );
            }

            if (!availableParmeters.TryGetValue(param.Name, out object? value))
            {
                if (param.IsOptional)
                {
                    return null;
                }
                else
                {
                    throw new ArgumentException(
                        $"No matching route parameter for \"{param.ParameterType.Name} {param.Name}\"", param.Name);
                }
            }

            if (!param.ParameterType.IsAssignableFrom(value.GetType()))
            {
                try
                {
                    value = Convert.ChangeType(value, param.ParameterType);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        $"Cannot assign type \"{value.GetType()}\" to parameter \"{param.ParameterType.Name} {param.Name}\"",
                        param.Name, ex);
                }
            }

            return value;
        }
    }
}